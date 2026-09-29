using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace WatchSearchDocs;

public partial class MainWindow : Window
{
    private readonly List<string> _rootFolderPaths = new();
    public ObservableCollection<FolderTreeNode> RootFolderNodes { get; } = new();
    private readonly List<ProcessedDocumentResult> _indexedResults = new();
    private CancellationTokenSource? _indexingCts;
    private bool _isLoading = false;
    private ProcessedDocumentResult? _currentDetailDoc = null;

    public MainWindow()
    {
        InitializeComponent();
        IcRootFolders.ItemsSource = RootFolderNodes;
        TvSubfolders.ItemsSource = RootFolderNodes;
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await DocumentIndexService.Instance.EnsureDatabaseCreatedAsync();

            var savedResults = await DocumentIndexService.Instance.GetAllIndexedResultsFromDbAsync();
            _indexedResults.Clear();
            _indexedResults.AddRange(savedResults);

            var savedRoots = await DocumentIndexService.Instance.GetAllRootsAsync();
            if (savedRoots.Count > 0 && _rootFolderPaths.Count == 0)
            {
                var validRoots = savedRoots.Where(r => r.IsActive).Select(r => r.RootPath).Where(Directory.Exists).ToList();
                if (validRoots.Count > 0)
                {
                    _rootFolderPaths.AddRange(validRoots);
                    await LoadAllRootsAsync();
                }
            }
        }
        catch { }
    }

    private async void BtnSelectFolders_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;

        var dialog = new OpenFolderDialog
        {
            Title = "Seleccionar Carpetas para Clasificar Documentos",
            Multiselect = true
        };

        if (dialog.ShowDialog() == true)
        {
            _rootFolderPaths.Clear();
            _rootFolderPaths.AddRange(dialog.FolderNames);

            foreach (var folder in dialog.FolderNames)
            {
                try { await DocumentIndexService.Instance.GetOrCreateRootAsync(folder); } catch { }
            }

            await LoadAllRootsAsync();
        }
    }

    private async void BtnReload_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;

        if (_rootFolderPaths.Count > 0)
        {
            await LoadAllRootsAsync();
        }
    }

    private void BtnToggleSubfolders_Click(object sender, RoutedEventArgs e)
    {
        FloatingSubfoldersPanel.Visibility = FloatingSubfoldersPanel.Visibility == Visibility.Visible 
            ? Visibility.Collapsed 
            : Visibility.Visible;
    }

    private void BtnCloseFloatingPanel_Click(object sender, RoutedEventArgs e)
    {
        FloatingSubfoldersPanel.Visibility = Visibility.Collapsed;
    }

    private void ChkShowHidden_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyFilter();
    }

    private void BtnExpandAll_Click(object sender, RoutedEventArgs e)
    {
        SetExpansionAll(true);
    }

    private void BtnCollapseAll_Click(object sender, RoutedEventArgs e)
    {
        SetExpansionAll(false);
    }

    private void SetExpansionAll(bool expanded)
    {
        foreach (var node in GetAllNodesFlat(RootFolderNodes))
        {
            node.IsExpanded = expanded;
        }
    }

    #region PROCESO DE INDEXACIÓN EN TIEMPO REAL (PDF CON PDFPIG)

    private async void BtnStartIndexing_Click(object sender, RoutedEventArgs e)
    {
        var allPdfFiles = GetAllNodesFlat(RootFolderNodes)
            .Where(folder => folder.IsSelected)
            .SelectMany(folder => folder.AllDirectFiles)
            .Where(file => file.IsIndexable && file.ProcessorTarget.Equals("PdfProcessor", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(file => file.FullPath)
            .ToList();

        if (allPdfFiles.Count == 0)
        {
            MessageBox.Show(
                "No se encontraron archivos PDF candidatos en las carpetas seleccionadas.\nSelecciona carpetas que contengan documentos .pdf para iniciar la prueba de indexación con PdfPig.",
                "Aviso de Indexación",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        FolderMainView.Visibility = Visibility.Collapsed;
        FilterRowBar.Visibility = Visibility.Collapsed;
        FloatingSubfoldersPanel.Visibility = Visibility.Collapsed;
        FloatingDemoPanel.Visibility = Visibility.Collapsed;
        FloatingContentDetailPanel.Visibility = Visibility.Collapsed;
        IndexingProgressView.Visibility = Visibility.Visible;
        BtnCancelIndexing.Visibility = Visibility.Visible;
        BtnCancelIndexing.IsEnabled = true;
        BtnContinueIndexing.Visibility = Visibility.Collapsed;

        _indexingCts = new CancellationTokenSource();
        var cancellationToken = _indexingCts.Token;

        int totalFiles = allPdfFiles.Count;
        int processedCount = 0;

        PbIndexing.Maximum = totalFiles;
        PbIndexing.Value = 0;
        TxtProgressPercent.Text = "0%";

        try
        {
            for (int i = 0; i < totalFiles; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var file = allPdfFiles[i];

                var result = await Task.Run(() => PdfProcessor.ProcessPdf(file.FullPath), cancellationToken);

                _indexedResults.RemoveAll(r => r.FullPath.Equals(file.FullPath, StringComparison.OrdinalIgnoreCase));
                _indexedResults.Add(result);

                try
                {
                    await DocumentIndexService.Instance.IndexProcessedResultAsync(file, result);
                }
                catch { }

                file.IndexingStatus = result.Success ? "Indexado" : "Error";

                processedCount++;

                PbIndexing.Value = processedCount;
                int percent = (int)Math.Round((double)processedCount * 100 / totalFiles);
                TxtProgressPercent.Text = $"{percent}%";
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            BtnCancelIndexing.Visibility = Visibility.Collapsed;
            BtnContinueIndexing.Visibility = Visibility.Visible;
        }
    }

    private void BtnCancelIndexing_Click(object sender, RoutedEventArgs e)
    {
        BtnCancelIndexing.IsEnabled = false;
        _indexingCts?.Cancel();
    }

    private void BtnContinueIndexing_Click(object sender, RoutedEventArgs e)
    {
        IndexingProgressView.Visibility = Visibility.Collapsed;
        FolderMainView.Visibility = Visibility.Visible;
        FilterRowBar.Visibility = Visibility.Visible;

        ApplyFilter();
    }

    #endregion

    #region PANELES FLOTANTES DEMO Y VISOR DE CONTENIDO CAPTADO

    private void BtnOpenDemoPanel_Click(object sender, RoutedEventArgs e)
    {
        FloatingDemoPanel.Visibility = FloatingDemoPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (FloatingDemoPanel.Visibility == Visibility.Visible)
        {
            FloatingSubfoldersPanel.Visibility = Visibility.Collapsed;

            LbIndexedDocuments.ItemsSource = null;
            LbIndexedDocuments.ItemsSource = _indexedResults.OrderBy(r => r.FileName).ToList();

            int totalPages = _indexedResults.Sum(r => r.PageCount);
            long totalChars = _indexedResults.Sum(r => r.TotalCharacters);
            TxtDemoStatsSummary.Text = $"{_indexedResults.Count} archivo(s) indexados con PdfPig ({totalPages} págs. totales, {totalChars:N0} caracteres extraídos)";
        }
    }

    private void BtnCloseDemoPanel_Click(object sender, RoutedEventArgs e)
    {
        FloatingDemoPanel.Visibility = Visibility.Collapsed;
    }

    private void LbIndexedDocuments_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LbIndexedDocuments.SelectedItem is ProcessedDocumentResult doc)
        {
            _currentDetailDoc = doc;
            OpenContentDetailPanel(doc);
        }
    }

    private void OpenContentDetailPanel(ProcessedDocumentResult doc)
    {
        TxtDetailDocTitle.Text = $"Contenido Captado - {doc.FileName}";
        TxtDetailDocStats.Text = $"{doc.ExtractionLevel} | Páginas: {doc.PageCount} | Imágenes: {doc.TotalImagesFound} ({doc.ImagesWithOcrText} con OCR) | Palabras: {doc.TotalWords:N0} | Caracteres: {doc.TotalCharacters:N0} | Tiempo: {doc.ProcessingTimeMs:N0} ms";

        CmbPageFilter.Items.Clear();
        CmbPageFilter.Items.Add("Todas las páginas");
        for (int i = 1; i <= doc.PageCount; i++)
        {
            CmbPageFilter.Items.Add($"Página {i}");
        }
        CmbPageFilter.SelectedIndex = 0;

        TxtExtractedContent.Text = doc.FullText;
        FloatingContentDetailPanel.Visibility = Visibility.Visible;
    }

    private void CmbPageFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentDetailDoc == null) return;

        int selectedIndex = CmbPageFilter.SelectedIndex;

        if (selectedIndex <= 0)
        {
            TxtExtractedContent.Text = _currentDetailDoc.FullText;
        }
        else
        {
            int pageNum = selectedIndex;
            var page = _currentDetailDoc.Pages.FirstOrDefault(p => p.PageNumber == pageNum);
            if (page != null)
            {
                TxtExtractedContent.Text = $"=== Página {page.PageNumber} (Palabras: {page.WordCount}, Caracteres: {page.CharacterCount}) ===\n\n{page.Text}";
            }
            else
            {
                TxtExtractedContent.Text = "Página sin texto detectable.";
            }
        }
    }

    private void BtnCloseDetailPanel_Click(object sender, RoutedEventArgs e)
    {
        FloatingContentDetailPanel.Visibility = Visibility.Collapsed;
    }

    #endregion

    #region CARGA Y ESTRUCTURA DE CARPETAS

    private async void BtnApplyFolders_Click(object sender, RoutedEventArgs e)
    {
        var allNodes = GetAllNodesFlat(RootFolderNodes).ToList();

        var restrictedSelected = allNodes
            .Where(f => f.IsRestricted && f.IsSelected && !f.HasBeenElevated)
            .ToList();

        if (restrictedSelected.Count > 0)
        {
            var folderListText = string.Join("\n - ", restrictedSelected.Select(f => $"{f.Name} ({f.FullPath})"));
            var message = $"Ha seleccionado {restrictedSelected.Count} carpeta(s) con acceso restringido por permisos de Windows:\n\n - {folderListText}\n\nPara leer su contenido se requieren permisos de Administrador.\n¿Desea solicitar permisos de Administrador para desbloquear su lectura?";

            var result = MessageBox.Show(message, "Acceso Restringido", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                await RequestElevatedScanAsync(restrictedSelected);
            }
        }

        FloatingSubfoldersPanel.Visibility = Visibility.Collapsed;
        ApplyFilter();
    }

    private async Task RequestElevatedScanAsync(List<FolderTreeNode> restrictedFolders)
    {
        try
        {
            PbLoading.Visibility = Visibility.Visible;

            string currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
            {
                MessageBox.Show("No se pudo localizar el ejecutable para la elevación de permisos.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            foreach (var folder in restrictedFolders)
            {
                string tempOutputFile = Path.Combine(Path.GetTempPath(), $"WatchSearchDocs_{Guid.NewGuid():N}.json");

                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = currentExe,
                        Arguments = $"--scan-elevated \"{folder.FullPath}\" \"{folder.RootPath}\" \"{tempOutputFile}\"",
                        Verb = "runas",
                        UseShellExecute = true,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(startInfo);
                    if (process != null)
                    {
                        await process.WaitForExitAsync();

                        if (process.ExitCode == 0 && File.Exists(tempOutputFile))
                        {
                            string json = await File.ReadAllTextAsync(tempOutputFile);
                            var scannedFiles = JsonSerializer.Deserialize<List<FileItem>>(json);

                            if (scannedFiles != null)
                            {
                                folder.AllDirectFiles.Clear();
                                foreach (var nf in scannedFiles.Where(f => f.IsIndexable))
                                {
                                    folder.AllDirectFiles.Add(nf);
                                }
                                folder.FileCount = folder.AllDirectFiles.Count;
                            }

                            folder.IsRestricted = false;
                            folder.HasBeenElevated = true;
                            folder.OnPropertyChanged(nameof(folder.TypeLabel));
                            folder.OnPropertyChanged(nameof(folder.FolderColor));
                            folder.OnPropertyChanged(nameof(folder.FileCountDisplay));
                        }
                    }
                }
                catch (Win32Exception)
                {
                    MessageBox.Show($"Permisos cancelados por el usuario para '{folder.Name}'. La carpeta permanecerá sin listar.", "Permisos no concedidos", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                finally
                {
                    if (File.Exists(tempOutputFile))
                    {
                        try { File.Delete(tempOutputFile); } catch { }
                    }
                }
            }

            UpdateSelectAllMasterState();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error durante la elevación de permisos:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            PbLoading.Visibility = Visibility.Collapsed;
        }
    }

    private async Task LoadAllRootsAsync()
    {
        try
        {
            _isLoading = true;
            PbLoading.Visibility = Visibility.Visible;

            RootFolderNodes.Clear();

            var (roots, omittedCount, prunedFolders) = await Task.Run(() =>
            {
                var loadedRoots = new List<FolderTreeNode>();
                int totalOmitted = 0;
                int totalPruned = 0;

                foreach (var rootPath in _rootFolderPaths)
                {
                    if (!Directory.Exists(rootPath)) continue;

                    var rootNode = BuildFolderTree(rootPath, rootPath, isRoot: true, ref totalOmitted, ref totalPruned);
                    if (rootNode != null)
                    {
                        loadedRoots.Add(rootNode);
                    }
                }

                return (loadedRoots, totalOmitted, totalPruned);
            });

            foreach (var node in roots)
            {
                RootFolderNodes.Add(node);
            }

            var allNodesFlat = GetAllNodesFlat(RootFolderNodes).ToList();
            int subfolderCount = allNodesFlat.Count(f => !f.IsRoot);

            if (subfolderCount > 0)
            {
                BtnToggleSubfolders.Visibility = Visibility.Visible;
                BtnToggleSubfolders.Content = $"Subcarpetas ({subfolderCount})";
                FloatingSubfoldersPanel.Visibility = Visibility.Visible;
            }
            else
            {
                BtnToggleSubfolders.Visibility = Visibility.Collapsed;
                FloatingSubfoldersPanel.Visibility = Visibility.Collapsed;
            }

            UpdateSelectAllMasterState();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error al cargar carpetas:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isLoading = false;
            PbLoading.Visibility = Visibility.Collapsed;
        }
    }

    private FolderTreeNode? BuildFolderTree(string currentPath, string rootPath, bool isRoot, ref int totalOmitted, ref int totalPruned)
    {
        var dirInfo = new DirectoryInfo(currentPath);
        string folderName = isRoot ? (string.IsNullOrEmpty(dirInfo.Name) ? currentPath : dirInfo.Name) : dirInfo.Name;

        if (!isRoot && ElevatedScanner.IsIgnoredDirectoryName(folderName))
        {
            totalPruned++;
            return null;
        }

        string relPath = isRoot ? "Raíz" : Path.GetRelativePath(rootPath, currentPath);

        bool restricted = false;

        try
        {
            var testEnum = dirInfo.EnumerateFileSystemInfos();
            using var enumerator = testEnum.GetEnumerator();
            enumerator.MoveNext();
        }
        catch (UnauthorizedAccessException)
        {
            restricted = true;
        }
        catch { }

        var node = new FolderTreeNode
        {
            Name = folderName,
            RelativePath = relPath,
            FullPath = currentPath,
            RootPath = rootPath,
            IsRoot = isRoot,
            IsRestricted = restricted,
            IsSelected = !restricted,
            IsExpanded = true
        };

        if (restricted)
        {
            return node;
        }

        try
        {
            foreach (var file in dirInfo.EnumerateFiles("*"))
            {
                if (ElevatedScanner.IsTemporaryFile(file))
                    continue;

                var classification = DocumentClassifier.Classify(file.FullName, file.Extension);

                if (!classification.IsIndexable)
                {
                    totalOmitted++;
                    continue;
                }

                bool isHidden = ElevatedScanner.IsHiddenOrSystem(file);
                bool isLocked = ElevatedScanner.CheckIfFileIsLocked(file.FullName);

                string initialStatus = _indexedResults.Any(r => r.FullPath.Equals(file.FullName, StringComparison.OrdinalIgnoreCase))
                    ? "Indexado"
                    : "Pendiente";

                node.AllDirectFiles.Add(new FileItem
                {
                    Name = file.Name,
                    FolderName = folderName,
                    FolderType = isRoot ? "Raíz" : "Subcarpeta",
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
                    IsIndexable = classification.IsIndexable,
                    CategoryName = classification.CategoryName,
                    ProcessorTarget = classification.ProcessorTarget,
                    ClassificationReason = classification.Reason,
                    IndexingStatus = initialStatus
                });
            }
            node.FileCount = node.AllDirectFiles.Count;
        }
        catch (UnauthorizedAccessException)
        {
            node.IsRestricted = true;
        }
        catch { }

        try
        {
            foreach (var subDir in dirInfo.EnumerateDirectories("*"))
            {
                if (subDir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;

                if (ElevatedScanner.IsIgnoredDirectoryName(subDir.Name))
                {
                    totalPruned++;
                    continue;
                }

                var childNodes = CollectSubfoldersWithFiles(subDir.FullName, rootPath, ref totalOmitted, ref totalPruned);
                foreach (var childNode in childNodes)
                {
                    node.Subfolders.Add(childNode);
                }
            }
        }
        catch { }

        bool hasRelevantContent = node.AllDirectFiles.Count > 0 || node.Subfolders.Count > 0 || node.IsRestricted;

        if (!isRoot && !hasRelevantContent)
        {
            totalPruned++;
            return null;
        }

        return node;
    }

    private List<FolderTreeNode> CollectSubfoldersWithFiles(string currentPath, string rootPath, ref int totalOmitted, ref int totalPruned)
    {
        var result = new List<FolderTreeNode>();
        var childNode = BuildFolderTree(currentPath, rootPath, isRoot: false, ref totalOmitted, ref totalPruned);
        if (childNode == null)
            return result;

        if (childNode.AllDirectFiles.Count > 0 || childNode.IsRestricted)
        {
            result.Add(childNode);
        }
        else
        {
            foreach (var grandChild in childNode.Subfolders)
            {
                result.Add(grandChild);
            }
        }

        return result;
    }

    private void ChkSelectAll_Click(object sender, RoutedEventArgs e)
    {
        bool select = ChkSelectAll.IsChecked == true;
        foreach (var node in GetAllNodesFlat(RootFolderNodes))
        {
            node.IsSelected = select;
        }

        UpdateSelectAllMasterState();
        ApplyFilter();
    }

    private void BtnDeselectAllFolders_Click(object sender, RoutedEventArgs e)
    {
        foreach (var node in GetAllNodesFlat(RootFolderNodes))
        {
            node.IsSelected = false;
        }

        UpdateSelectAllMasterState();
        ApplyFilter();
    }

    private void FolderCheckBox_Click(object sender, RoutedEventArgs e)
    {
        UpdateSelectAllMasterState();
        ApplyFilter();
    }

    private void UpdateSelectAllMasterState()
    {
        if (ChkSelectAll == null || TxtSubfoldersSummary == null) return;

        var allNodes = GetAllNodesFlat(RootFolderNodes).ToList();
        int total = allNodes.Count;
        int selected = allNodes.Count(f => f.IsSelected);

        if (total == 0)
        {
            ChkSelectAll.IsChecked = false;
        }
        else if (selected == total)
        {
            ChkSelectAll.IsChecked = true;
        }
        else if (selected == 0)
        {
            ChkSelectAll.IsChecked = false;
        }
        else
        {
            ChkSelectAll.IsChecked = null;
        }

        TxtSubfoldersSummary.Text = $"{selected} de {total} carpetas seleccionadas";
    }

    private void ApplyFilter()
    {
        if (TxtCount == null || TxtEmptyNotice == null) return;

        bool showHidden = ChkShowHidden?.IsChecked == true;

        int totalVisibleFiles = 0;

        foreach (var root in RootFolderNodes)
        {
            totalVisibleFiles += FilterNodeRecursive(root, showHidden);
        }

        TxtCount.Text = $"{totalVisibleFiles} archivo(s) indexables";

        bool hasAnyRoot = RootFolderNodes.Count > 0;
        if (!hasAnyRoot)
        {
            TxtEmptyNotice.Visibility = Visibility.Visible;
            TxtEmptyNotice.Text = "Selecciona una o más carpetas para clasificar y ver archivos indexables.";
        }
        else if (totalVisibleFiles == 0)
        {
            TxtEmptyNotice.Visibility = Visibility.Visible;
            TxtEmptyNotice.Text = "No se encontraron archivos indexables en las carpetas seleccionadas.";
        }
        else
        {
            TxtEmptyNotice.Visibility = Visibility.Collapsed;
        }
    }

    private int FilterNodeRecursive(FolderTreeNode node, bool showHidden)
    {
        node.FilteredFiles.Clear();

        if (node.IsSelected)
        {
            var files = node.AllDirectFiles.Where(f => f.IsIndexable);

            if (!showHidden)
            {
                files = files.Where(f => !f.IsHidden);
            }

            foreach (var f in files)
            {
                node.FilteredFiles.Add(f);
            }
        }

        int count = node.FilteredFiles.Count;

        foreach (var child in node.Subfolders)
        {
            count += FilterNodeRecursive(child, showHidden);
        }

        node.IsVisible = node.IsSelected || node.Subfolders.Any(s => s.IsVisible);

        node.OnPropertyChanged(nameof(node.HasDirectFiles));
        node.OnPropertyChanged(nameof(node.HasSubfolders));

        return count;
    }

    private static IEnumerable<FolderTreeNode> GetAllNodesFlat(IEnumerable<FolderTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in GetAllNodesFlat(node.Subfolders))
            {
                yield return child;
            }
        }
    }

    #endregion
}