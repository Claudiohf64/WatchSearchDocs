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
    private int _totalOmittedNonIndexable = 0;
    private ProcessedDocumentResult? _currentDetailDoc = null;

    public MainWindow()
    {
        InitializeComponent();
        IcRootFolders.ItemsSource = RootFolderNodes;
        TvSubfolders.ItemsSource = RootFolderNodes;
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
            UpdateFolderPathTextBox();
            await LoadAllRootsAsync();
        }
    }

    private async void BtnAddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;

        var dialog = new OpenFolderDialog
        {
            Title = "Agregar más Carpetas al Clasificador",
            Multiselect = true
        };

        if (dialog.ShowDialog() == true)
        {
            foreach (var path in dialog.FolderNames)
            {
                if (!_rootFolderPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    _rootFolderPaths.Add(path);
                }
            }
            UpdateFolderPathTextBox();
            await LoadAllRootsAsync();
        }
    }

    private void UpdateFolderPathTextBox()
    {
        if (_rootFolderPaths.Count == 0)
        {
            TxtFolderPath.Text = "Ninguna carpeta seleccionada";
            TxtFolderPath.ToolTip = "Carpetas seleccionadas";
        }
        else if (_rootFolderPaths.Count == 1)
        {
            TxtFolderPath.Text = _rootFolderPaths[0];
            TxtFolderPath.ToolTip = _rootFolderPaths[0];
        }
        else
        {
            TxtFolderPath.Text = $"{_rootFolderPaths.Count} carpetas seleccionadas ({string.Join(", ", _rootFolderPaths.Select(Path.GetFileName))})";
            TxtFolderPath.ToolTip = string.Join(Environment.NewLine, _rootFolderPaths);
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
        // 1. Obtener todos los archivos PDF candidatos de las carpetas seleccionadas
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

        // 2. Cambiar de pantalla a la vista de proceso en tiempo real
        FolderMainView.Visibility = Visibility.Collapsed;
        FilterRowBar.Visibility = Visibility.Collapsed;
        FloatingSubfoldersPanel.Visibility = Visibility.Collapsed;
        FloatingDemoPanel.Visibility = Visibility.Collapsed;
        FloatingContentDetailPanel.Visibility = Visibility.Collapsed;
        IndexingProgressView.Visibility = Visibility.Visible;
        TxtIndexingResultSummary.Visibility = Visibility.Collapsed;
        BtnCancelIndexing.Visibility = Visibility.Visible;
        BtnCancelIndexing.IsEnabled = true;
        BtnContinueIndexing.Visibility = Visibility.Collapsed;

        _indexingCts = new CancellationTokenSource();
        var cancellationToken = _indexingCts.Token;

        var stopwatch = Stopwatch.StartNew();
        int totalFiles = allPdfFiles.Count;
        int processedCount = 0;
        int successCount = 0;

        PbIndexing.Maximum = totalFiles;
        PbIndexing.Value = 0;
        TxtProgressPercent.Text = "0%";
        TxtMetricProcessedCount.Text = $"0 / {totalFiles}";
        TxtMetricElapsedTime.Text = "00:00:00";
        TxtMetricSpeed.Text = "0.0 arch/s";
        TxtMetricEstimatedRemaining.Text = "Calculando...";

        try
        {
            for (int i = 0; i < totalFiles; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var file = allPdfFiles[i];
                TxtCurrentProcessingFile.Text = $"{file.Name} ({file.FullPath})";

                // Ejecutar extracción con PdfPig en segundo plano
                var result = await Task.Run(() => PdfProcessor.ProcessPdf(file.FullPath), cancellationToken);

                // Guardar en la colección global de resultados indexados
                _indexedResults.RemoveAll(r => r.FullPath.Equals(file.FullPath, StringComparison.OrdinalIgnoreCase));
                _indexedResults.Add(result);

                // Actualizar el estado del archivo en la tabla de carpetas
                file.IndexingStatus = result.Success ? "Indexado" : "Error";

                processedCount++;
                if (result.Success) successCount++;

                // Cálculos en tiempo real
                double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                double filesPerSec = elapsedSeconds > 0.05 ? (double)processedCount / elapsedSeconds : processedCount;
                int remaining = totalFiles - processedCount;
                double remainingSeconds = filesPerSec > 0 ? remaining / filesPerSec : 0;

                // Actualizar interfaz
                PbIndexing.Value = processedCount;
                int percent = (int)Math.Round((double)processedCount * 100 / totalFiles);
                TxtProgressPercent.Text = $"{percent}%";
                TxtMetricProcessedCount.Text = $"{processedCount} / {totalFiles}";
                TxtMetricElapsedTime.Text = stopwatch.Elapsed.ToString(@"hh\:mm\:ss");
                TxtMetricSpeed.Text = $"{filesPerSec:F1} arch/s";
                TxtMetricEstimatedRemaining.Text = TimeSpan.FromSeconds(remainingSeconds).ToString(@"hh\:mm\:ss");
            }

            stopwatch.Stop();

            double finalElapsedSec = stopwatch.Elapsed.TotalSeconds;
            double finalSpeed = finalElapsedSec > 0 ? (double)processedCount / finalElapsedSec : processedCount;

            TxtCurrentProcessingFile.Text = "Proceso finalizado. Haz clic en 'Continuar' para regresar a la vista de carpetas.";
            TxtIndexingResultSummary.Visibility = Visibility.Visible;
            TxtIndexingResultSummary.Text = $"Indexación completada: {successCount} de {totalFiles} PDFs procesados en {stopwatch.Elapsed.TotalSeconds:F2} seg (Promedio: {finalSpeed:F1} arch/s).";
        }
        catch (OperationCanceledException)
        {
            TxtCurrentProcessingFile.Text = "Indexación cancelada.";
            TxtIndexingResultSummary.Visibility = Visibility.Visible;
            TxtIndexingResultSummary.Text = $"Indexación cancelada por el usuario ({processedCount} de {totalFiles} procesados).";
        }
        finally
        {
            // Mostrar botón de continuar para que el usuario decida cuándo salir
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
        // Volver a la pantalla de carpetas indicando qué archivos fueron procesados
        IndexingProgressView.Visibility = Visibility.Collapsed;
        FolderMainView.Visibility = Visibility.Visible;
        FilterRowBar.Visibility = Visibility.Visible;

        ApplyFilter();
        TxtStatus.Text = $"Indexación lista: {_indexedResults.Count} PDF(s) procesados. Haz clic en 'Panel Demo Processor' para consultar el texto captado.";
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

            // Actualizar lista de documentos procesados
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

        // Cargar páginas en el selector
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
            int pageNum = selectedIndex; // Página 1 en adelante
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
            else
            {
                TxtStatus.Text = "Operación completada sin solicitar elevación de permisos.";
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
            TxtStatus.Text = "Solicitando permisos de Administrador para leer carpetas restringidas...";

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
            TxtStatus.Text = "Proceso de lectura con permisos completado.";
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
            TxtStatus.Text = "Clasificando archivos y optimizando estructura de carpetas relevantes...";

            RootFolderNodes.Clear();
            _totalOmittedNonIndexable = 0;

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

            _totalOmittedNonIndexable = omittedCount;

            foreach (var node in roots)
            {
                RootFolderNodes.Add(node);
            }

            var allNodesFlat = GetAllNodesFlat(RootFolderNodes).ToList();
            int subfolderCount = allNodesFlat.Count(f => !f.IsRoot);
            int restrictedCount = allNodesFlat.Count(f => f.IsRestricted);

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

            int totalIndexableFiles = allNodesFlat.Sum(f => f.AllDirectFiles.Count);
            string msg = $"Carga optimizada: {totalIndexableFiles} archivo(s) indexables en {subfolderCount} subcarpeta(s) relevantes";
            if (prunedFolders > 0) msg += $" ({prunedFolders} carpetas vacías o irrelevantes ignoradas)";
            if (restrictedCount > 0) msg += $" [{restrictedCount} restringidas]";
            TxtStatus.Text = msg;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error al cargar carpetas:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            TxtStatus.Text = $"Error: {ex.Message}";
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

        // Si no es raíz y es un directorio de ruido técnico (node_modules, .git, bin, obj, etc.), ignorar inmediatamente
        if (!isRoot && ElevatedScanner.IsIgnoredDirectoryName(folderName))
        {
            totalPruned++;
            return null;
        }

        string relPath = isRoot ? "(raíz)" : Path.GetRelativePath(rootPath, currentPath);

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

                // Si ya fue indexado previamente en esta sesión, conservar su estado
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

                var childNode = BuildFolderTree(subDir.FullName, rootPath, isRoot: false, ref totalOmitted, ref totalPruned);
                if (childNode != null)
                {
                    node.Subfolders.Add(childNode);
                }
            }
        }
        catch { }

        // 3. OPTIMIZACIÓN Y PODADO DE ÁRBOL:
        // Una subcarpeta solo se conserva si:
        // - Contiene al menos un archivo indexable directo, O
        // - Alguna de sus subcarpetas contiene archivos indexables, O
        // - Es de acceso restringido.
        // Si no cumple ninguna de estas condiciones y no es raíz, se descarta para no sobrecargar memoria ni la UI.
        bool hasRelevantContent = node.AllDirectFiles.Count > 0 || node.Subfolders.Count > 0 || node.IsRestricted;

        if (!isRoot && !hasRelevantContent)
        {
            totalPruned++;
            return null;
        }

        return node;
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

    private void BtnSelectAllFolders_Click(object sender, RoutedEventArgs e)
    {
        foreach (var node in GetAllNodesFlat(RootFolderNodes))
        {
            node.IsSelected = true;
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

    private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filter = TxtFilter?.Text?.Trim();
        bool showHidden = ChkShowHidden?.IsChecked == true;

        int totalVisibleFiles = 0;

        foreach (var root in RootFolderNodes)
        {
            totalVisibleFiles += FilterNodeRecursive(root, filter, showHidden);
        }

        if (_totalOmittedNonIndexable > 0)
        {
            TxtCount.Text = $"{totalVisibleFiles} indexables ({_totalOmittedNonIndexable} omitidos)";
        }
        else
        {
            TxtCount.Text = $"{totalVisibleFiles} archivo(s) indexables";
        }

        bool hasAnyRoot = RootFolderNodes.Count > 0;
        if (!hasAnyRoot)
        {
            TxtEmptyNotice.Visibility = Visibility.Visible;
            TxtEmptyNotice.Text = "Selecciona una o más carpetas para clasificar y ver archivos indexables.";
        }
        else if (totalVisibleFiles == 0)
        {
            TxtEmptyNotice.Visibility = Visibility.Visible;
            TxtEmptyNotice.Text = string.IsNullOrEmpty(filter)
                ? "No se encontraron archivos compatibles con los procesadores de indexación en las carpetas seleccionadas."
                : "No se encontraron archivos indexables que coincidan con la búsqueda.";
        }
        else
        {
            TxtEmptyNotice.Visibility = Visibility.Collapsed;
        }
    }

    private int FilterNodeRecursive(FolderTreeNode node, string? filter, bool showHidden)
    {
        node.FilteredFiles.Clear();

        if (node.IsSelected)
        {
            var files = node.AllDirectFiles.Where(f => f.IsIndexable);

            if (!showHidden)
            {
                files = files.Where(f => !f.IsHidden);
            }

            if (!string.IsNullOrEmpty(filter))
            {
                files = files.Where(f =>
                    f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    f.Extension.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    f.FolderName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    f.CategoryName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    f.ProcessorTarget.Contains(filter, StringComparison.OrdinalIgnoreCase)
                );
            }

            foreach (var f in files)
            {
                node.FilteredFiles.Add(f);
            }
        }

        int count = node.FilteredFiles.Count;

        foreach (var child in node.Subfolders)
        {
            count += FilterNodeRecursive(child, filter, showHidden);
        }

        if (string.IsNullOrEmpty(filter))
        {
            node.IsVisible = node.IsSelected || node.Subfolders.Any(s => s.IsVisible);
        }
        else
        {
            node.IsVisible = (node.FilteredFiles.Count > 0) || node.Subfolders.Any(s => s.IsVisible);
        }

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