using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace WatchSearchDocs;

public class MainViewModel : ViewModelBase
{
    private readonly IIndexService _indexService;
    private readonly IDocumentProcessor _pdfProcessor;

    private string _statusMessage = "Listo. Selecciona carpetas para clasificar e indexar documentos.";
    private string _selectedFolderPathText = "Ninguna carpeta seleccionada";
    private int _totalDirectFilesCount;
    private int _totalOmittedFilesCount;
    private int _totalPrunedFoldersCount;
    private bool _isLoading;
    private bool _isFloatingDemoPanelVisible;
    private bool _isContentDetailPanelVisible;
    private ProcessedDocumentResult? _currentDetailDoc;
    private string _filterSearchTerm = string.Empty;
    private string _selectedCategoryFilter = "Todas las categorías";
    private CancellationTokenSource? _indexingCts;

    public ObservableCollection<FolderTreeNode> RootFolderNodes { get; } = new();
    public ObservableCollection<ProcessedDocumentResult> IndexedResults { get; } = new();
    public List<string> RootFolderPaths { get; } = new();

    public IndexingProgressViewModel IndexingProgress { get; } = new();

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string SelectedFolderPathText
    {
        get => _selectedFolderPathText;
        set => SetProperty(ref _selectedFolderPathText, value);
    }

    public int TotalDirectFilesCount
    {
        get => _totalDirectFilesCount;
        set => SetProperty(ref _totalDirectFilesCount, value);
    }

    public int TotalOmittedFilesCount
    {
        get => _totalOmittedFilesCount;
        set => SetProperty(ref _totalOmittedFilesCount, value);
    }

    public int TotalPrunedFoldersCount
    {
        get => _totalPrunedFoldersCount;
        set => SetProperty(ref _totalPrunedFoldersCount, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public bool IsFloatingDemoPanelVisible
    {
        get => _isFloatingDemoPanelVisible;
        set => SetProperty(ref _isFloatingDemoPanelVisible, value);
    }

    public bool IsContentDetailPanelVisible
    {
        get => _isContentDetailPanelVisible;
        set => SetProperty(ref _isContentDetailPanelVisible, value);
    }

    public ProcessedDocumentResult? CurrentDetailDoc
    {
        get => _currentDetailDoc;
        set => SetProperty(ref _currentDetailDoc, value);
    }

    public string FilterSearchTerm
    {
        get => _filterSearchTerm;
        set
        {
            if (SetProperty(ref _filterSearchTerm, value))
            {
                ApplyFilter();
            }
        }
    }

    public string SelectedCategoryFilter
    {
        get => _selectedCategoryFilter;
        set
        {
            if (SetProperty(ref _selectedCategoryFilter, value))
            {
                ApplyFilter();
            }
        }
    }

    public ICommand StartIndexingCommand { get; }
    public ICommand OpenDemoPanelCommand { get; }
    public ICommand CloseDemoPanelCommand { get; }
    public ICommand CloseContentDetailCommand { get; }

    public MainViewModel(IIndexService? indexService = null, IDocumentProcessor? pdfProcessor = null)
    {
        _indexService = indexService ?? DocumentIndexService.Instance;
        _pdfProcessor = pdfProcessor ?? PdfProcessor.Instance;

        StartIndexingCommand = new RelayCommand(async () => await ExecuteStartIndexingAsync(), () => !IsLoading);
        OpenDemoPanelCommand = new RelayCommand(() => IsFloatingDemoPanelVisible = true);
        CloseDemoPanelCommand = new RelayCommand(() => IsFloatingDemoPanelVisible = false);
        CloseContentDetailCommand = new RelayCommand(() => IsContentDetailPanelVisible = false);

        IndexingProgress.OnCancelled += () => _indexingCts?.Cancel();
        IndexingProgress.OnContinue += () =>
        {
            StatusMessage = $"Indexación lista: {IndexedResults.Count} archivo(s) procesados. Consulta los resultados en el Panel Demo.";
            ApplyFilter();
        };
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _indexService.EnsureDatabaseCreatedAsync();

            var saved = await _indexService.GetAllIndexedResultsFromDbAsync();
            IndexedResults.Clear();
            foreach (var item in saved)
            {
                IndexedResults.Add(item);
            }

            var roots = await _indexService.GetAllRootsAsync();
            if (roots.Count > 0 && RootFolderPaths.Count == 0)
            {
                var validPaths = roots.Where(r => r.IsActive).Select(r => r.RootPath).Where(Directory.Exists).ToList();
                if (validPaths.Count > 0)
                {
                    RootFolderPaths.AddRange(validPaths);
                    UpdateFolderPathDisplay();
                    await ScanAllRootsAsync();
                }
            }

            if (IndexedResults.Count > 0)
            {
                StatusMessage = $"Base de datos SQLite activa: {IndexedResults.Count} documento(s) indexados previamente.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Aviso de base de datos: {ex.Message}";
        }
    }

    public void UpdateFolderPathDisplay()
    {
        if (RootFolderPaths.Count == 0)
        {
            SelectedFolderPathText = "Ninguna carpeta seleccionada";
        }
        else if (RootFolderPaths.Count == 1)
        {
            SelectedFolderPathText = RootFolderPaths[0];
        }
        else
        {
            SelectedFolderPathText = $"{RootFolderPaths.Count} carpetas seleccionadas ({string.Join(", ", RootFolderPaths.Select(Path.GetFileName))})";
        }
    }

    public async Task ScanAllRootsAsync()
    {
        if (RootFolderPaths.Count == 0) return;

        IsLoading = true;
        StatusMessage = "Explorando y clasificando documentos...";

        try
        {
            var sw = Stopwatch.StartNew();

            var loadedRoots = new List<FolderTreeNode>();
            int omitted = 0;
            int pruned = 0;

            await Task.Run(() =>
            {
                foreach (var rootPath in RootFolderPaths)
                {
                    if (Directory.Exists(rootPath))
                    {
                        var (node, o, p) = ScanRootNode(rootPath);
                        if (node != null)
                        {
                            loadedRoots.Add(node);
                        }
                        omitted += o;
                        pruned += p;
                    }
                }
            });

            RootFolderNodes.Clear();
            foreach (var r in loadedRoots)
            {
                RootFolderNodes.Add(r);
            }

            TotalOmittedFilesCount = omitted;
            TotalPrunedFoldersCount = pruned;
            TotalDirectFilesCount = GetAllCandidateFiles().Count;

            sw.Stop();
            StatusMessage = $"Exploración completada en {sw.ElapsedMilliseconds} ms. {TotalDirectFilesCount} archivos indexables encontrados.";
            ApplyFilter();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private (FolderTreeNode? Node, int Omitted, int Pruned) ScanRootNode(string rootPath)
    {
        int omitted = 0;
        int pruned = 0;

        var node = new FolderTreeNode
        {
            Name = Path.GetFileName(rootPath),
            RelativePath = "Raíz",
            FullPath = rootPath,
            RootPath = rootPath,
            IsRoot = true,
            IsSelected = true,
            IsExpanded = true
        };

        if (string.IsNullOrEmpty(node.Name))
            node.Name = rootPath;

        try
        {
            var dirInfo = new DirectoryInfo(rootPath);
            foreach (var file in dirInfo.EnumerateFiles())
            {
                if (ElevatedScanner.IsTemporaryFile(file)) continue;

                var classification = DocumentClassifier.Classify(file.FullName, file.Extension);
                if (!classification.IsIndexable)
                {
                    omitted++;
                    continue;
                }

                bool isHidden = ElevatedScanner.IsHiddenOrSystem(file);
                bool isLocked = ElevatedScanner.CheckIfFileIsLocked(file.FullName);

                string initialStatus = IndexedResults.Any(r => r.FullPath.Equals(file.FullName, StringComparison.OrdinalIgnoreCase))
                    ? "Indexado"
                    : "Pendiente";

                node.AllDirectFiles.Add(new FileItem
                {
                    Name = file.Name,
                    FolderName = node.Name,
                    FolderType = "Raíz",
                    RelativeDirectory = "Raíz",
                    Extension = classification.DetectedExtension,
                    SizeBytes = file.Length,
                    SizeFormatted = ElevatedScanner.FormatFileSize(file.Length),
                    LastModified = file.LastWriteTime,
                    LastModifiedFormatted = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    FullPath = file.FullName,
                    RootPath = rootPath,
                    IsHidden = isHidden,
                    IsLocked = isLocked,
                    IsIndexable = true,
                    CategoryName = classification.CategoryName,
                    ProcessorTarget = classification.ProcessorTarget,
                    ClassificationReason = classification.Reason,
                    IndexingStatus = initialStatus
                });
            }

            node.FileCount = node.AllDirectFiles.Count;

            var subfolders = CollectSubfoldersWithFiles(dirInfo, rootPath, ref omitted, ref pruned);
            foreach (var sub in subfolders)
            {
                node.Subfolders.Add(sub);
            }
        }
        catch (UnauthorizedAccessException)
        {
            node.IsRestricted = true;
        }
        catch { }

        return (node, omitted, pruned);
    }

    private List<FolderTreeNode> CollectSubfoldersWithFiles(DirectoryInfo dir, string rootPath, ref int omitted, ref int pruned)
    {
        var result = new List<FolderTreeNode>();
        try
        {
            foreach (var sub in dir.EnumerateDirectories())
            {
                if (ElevatedScanner.IsIgnoredDirectoryName(sub.Name))
                {
                    pruned++;
                    continue;
                }

                var (node, o, p) = ScanSubdirectory(sub, rootPath);
                omitted += o;
                pruned += p;

                if (node != null)
                {
                    if (node.FileCount > 0)
                    {
                        result.Add(node);
                    }
                    else if (node.Subfolders.Count > 0)
                    {
                        result.AddRange(node.Subfolders);
                    }
                }
            }
        }
        catch { }
        return result;
    }

    private (FolderTreeNode? Node, int Omitted, int Pruned) ScanSubdirectory(DirectoryInfo dir, string rootPath)
    {
        int omitted = 0;
        int pruned = 0;

        string relPath = Path.GetRelativePath(rootPath, dir.FullName);

        var node = new FolderTreeNode
        {
            Name = dir.Name,
            RelativePath = relPath,
            FullPath = dir.FullName,
            RootPath = rootPath,
            IsRoot = false,
            IsSelected = true,
            IsExpanded = true
        };

        try
        {
            foreach (var file in dir.EnumerateFiles())
            {
                if (ElevatedScanner.IsTemporaryFile(file)) continue;

                var classification = DocumentClassifier.Classify(file.FullName, file.Extension);
                if (!classification.IsIndexable)
                {
                    omitted++;
                    continue;
                }

                bool isHidden = ElevatedScanner.IsHiddenOrSystem(file);
                bool isLocked = ElevatedScanner.CheckIfFileIsLocked(file.FullName);

                string initialStatus = IndexedResults.Any(r => r.FullPath.Equals(file.FullName, StringComparison.OrdinalIgnoreCase))
                    ? "Indexado"
                    : "Pendiente";

                node.AllDirectFiles.Add(new FileItem
                {
                    Name = file.Name,
                    FolderName = dir.Name,
                    FolderType = "Subcarpeta",
                    RelativeDirectory = relPath,
                    Extension = classification.DetectedExtension,
                    SizeBytes = file.Length,
                    SizeFormatted = ElevatedScanner.FormatFileSize(file.Length),
                    LastModified = file.LastWriteTime,
                    LastModifiedFormatted = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    FullPath = file.FullName,
                    RootPath = rootPath,
                    IsHidden = isHidden,
                    IsLocked = isLocked,
                    IsIndexable = true,
                    CategoryName = classification.CategoryName,
                    ProcessorTarget = classification.ProcessorTarget,
                    ClassificationReason = classification.Reason,
                    IndexingStatus = initialStatus
                });
            }

            node.FileCount = node.AllDirectFiles.Count;

            var subfolders = CollectSubfoldersWithFiles(dir, rootPath, ref omitted, ref pruned);
            foreach (var sub in subfolders)
            {
                node.Subfolders.Add(sub);
            }
        }
        catch (UnauthorizedAccessException)
        {
            node.IsRestricted = true;
        }
        catch { }

        return (node, omitted, pruned);
    }

    public List<FileItem> GetAllCandidateFiles()
    {
        var files = new List<FileItem>();
        void Collect(FolderTreeNode node)
        {
            if (node.IsSelected)
            {
                files.AddRange(node.AllDirectFiles);
            }
            foreach (var sub in node.Subfolders)
            {
                Collect(sub);
            }
        }

        foreach (var root in RootFolderNodes)
        {
            Collect(root);
        }

        return files;
    }

    public void ApplyFilter()
    {
        string term = FilterSearchTerm.Trim().ToLowerInvariant();
        string cat = SelectedCategoryFilter;

        void FilterNode(FolderTreeNode node)
        {
            node.FilteredFiles.Clear();
            foreach (var f in node.AllDirectFiles)
            {
                bool matchesTerm = string.IsNullOrEmpty(term) ||
                                   f.Name.ToLowerInvariant().Contains(term) ||
                                   f.Extension.ToLowerInvariant().Contains(term) ||
                                   f.FolderName.ToLowerInvariant().Contains(term);

                bool matchesCat = cat == "Todas las categorías" ||
                                  f.CategoryName.Equals(cat, StringComparison.OrdinalIgnoreCase);

                if (matchesTerm && matchesCat)
                {
                    node.FilteredFiles.Add(f);
                }
            }

            foreach (var sub in node.Subfolders)
            {
                FilterNode(sub);
            }
        }

        foreach (var root in RootFolderNodes)
        {
            FilterNode(root);
        }
    }

    private async Task ExecuteStartIndexingAsync()
    {
        var allPdfFiles = GetAllCandidateFiles()
            .Where(f => f.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (allPdfFiles.Count == 0)
        {
            StatusMessage = "No se encontraron archivos PDF candidatos para indexar.";
            return;
        }

        _indexingCts = new CancellationTokenSource();
        var token = _indexingCts.Token;

        IndexingProgress.Start(allPdfFiles.Count);

        var stopwatch = Stopwatch.StartNew();
        int totalFiles = allPdfFiles.Count;
        int processed = 0;
        int success = 0;

        try
        {
            for (int i = 0; i < totalFiles; i++)
            {
                if (token.IsCancellationRequested)
                    break;

                var file = allPdfFiles[i];
                IndexingProgress.UpdateMetrics(processed, totalFiles, stopwatch.Elapsed.TotalSeconds, $"{file.Name} ({file.FullPath})");

                var result = await _pdfProcessor.ProcessAsync(file.FullPath, token);

                var existing = IndexedResults.FirstOrDefault(r => r.FullPath.Equals(file.FullPath, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    IndexedResults.Remove(existing);
                }
                IndexedResults.Add(result);

                try
                {
                    await _indexService.IndexProcessedResultAsync(file, result);
                }
                catch { }

                file.IndexingStatus = result.Success ? "Indexado" : "Error";

                processed++;
                if (result.Success) success++;

                IndexingProgress.UpdateMetrics(processed, totalFiles, stopwatch.Elapsed.TotalSeconds, $"{file.Name} ({file.FullPath})");
                IndexingProgress.SetTime(stopwatch.Elapsed);
            }

            stopwatch.Stop();
            double finalElapsed = stopwatch.Elapsed.TotalSeconds;
            double finalSpeed = finalElapsed > 0 ? (double)processed / finalElapsed : processed;

            IndexingProgress.Complete(success, totalFiles, finalElapsed, finalSpeed);
        }
        catch (OperationCanceledException)
        {
            IndexingProgress.MarkCancelled(processed, totalFiles);
        }
    }
}
