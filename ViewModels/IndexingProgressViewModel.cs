using System;
using System.Diagnostics;
using System.Windows.Input;

namespace WatchSearchDocs;

public class IndexingProgressViewModel : ViewModelBase
{
    private bool _isVisible;
    private int _totalFiles;
    private int _processedCount;
    private int _progressPercent;
    private string _elapsedTimeFormatted = "00:00:00";
    private string _speedFormatted = "0.0 arch/s";
    private string _estimatedRemainingFormatted = "Calculando...";
    private string _currentFileText = string.Empty;
    private string _resultSummaryText = string.Empty;
    private bool _isResultSummaryVisible;
    private bool _canCancel = true;
    private bool _isContinueVisible;

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public int TotalFiles
    {
        get => _totalFiles;
        set => SetProperty(ref _totalFiles, value);
    }

    public int ProcessedCount
    {
        get => _processedCount;
        set => SetProperty(ref _processedCount, value);
    }

    public int ProgressPercent
    {
        get => _progressPercent;
        set => SetProperty(ref _progressPercent, value);
    }

    public string ElapsedTimeFormatted
    {
        get => _elapsedTimeFormatted;
        set => SetProperty(ref _elapsedTimeFormatted, value);
    }

    public string SpeedFormatted
    {
        get => _speedFormatted;
        set => SetProperty(ref _speedFormatted, value);
    }

    public string EstimatedRemainingFormatted
    {
        get => _estimatedRemainingFormatted;
        set => SetProperty(ref _estimatedRemainingFormatted, value);
    }

    public string CurrentFileText
    {
        get => _currentFileText;
        set => SetProperty(ref _currentFileText, value);
    }

    public string ResultSummaryText
    {
        get => _resultSummaryText;
        set => SetProperty(ref _resultSummaryText, value);
    }

    public bool IsResultSummaryVisible
    {
        get => _isResultSummaryVisible;
        set => SetProperty(ref _isResultSummaryVisible, value);
    }

    public bool CanCancel
    {
        get => _canCancel;
        set => SetProperty(ref _canCancel, value);
    }

    public bool IsContinueVisible
    {
        get => _isContinueVisible;
        set => SetProperty(ref _isContinueVisible, value);
    }

    public ICommand CancelCommand { get; }
    public ICommand ContinueCommand { get; }

    public event Action? OnCancelled;
    public event Action? OnContinue;

    public IndexingProgressViewModel()
    {
        CancelCommand = new RelayCommand(() =>
        {
            CanCancel = false;
            OnCancelled?.Invoke();
        }, () => CanCancel);

        ContinueCommand = new RelayCommand(() =>
        {
            IsVisible = false;
            OnContinue?.Invoke();
        }, () => IsContinueVisible);
    }

    public void Start(int total)
    {
        TotalFiles = total;
        ProcessedCount = 0;
        ProgressPercent = 0;
        ElapsedTimeFormatted = "00:00:00";
        SpeedFormatted = "0.0 arch/s";
        EstimatedRemainingFormatted = "Calculando...";
        CurrentFileText = "Iniciando indexación...";
        ResultSummaryText = string.Empty;
        IsResultSummaryVisible = false;
        CanCancel = true;
        IsContinueVisible = false;
        IsVisible = true;
    }

    public void UpdateMetrics(int processed, int total, double elapsedSeconds, string currentFilePath)
    {
        ProcessedCount = processed;
        TotalFiles = total;
        ProgressPercent = total > 0 ? (int)Math.Round((double)processed * 100 / total) : 0;
        CurrentFileText = currentFilePath;

        double speed = elapsedSeconds > 0.05 ? (double)processed / elapsedSeconds : processed;
        SpeedFormatted = $"{speed:F1} arch/s";

        int remaining = Math.Max(0, total - processed);
        double remainingSec = speed > 0 ? remaining / speed : 0;
        EstimatedRemainingFormatted = TimeSpan.FromSeconds(remainingSec).ToString(@"hh\:mm\:ss");
    }

    public void SetTime(TimeSpan elapsed)
    {
        ElapsedTimeFormatted = elapsed.ToString(@"hh\:mm\:ss");
    }

    public void Complete(int successCount, int total, double elapsedSec, double finalSpeed)
    {
        CanCancel = false;
        IsContinueVisible = true;
        CurrentFileText = "Proceso finalizado. Haz clic en 'Continuar' para regresar a la vista de carpetas.";
        ResultSummaryText = $"Indexación completada: {successCount} de {total} archivos procesados en {elapsedSec:F2} seg (Promedio: {finalSpeed:F1} arch/s).";
        IsResultSummaryVisible = true;
    }

    public void MarkCancelled(int processed, int total)
    {
        CanCancel = false;
        IsContinueVisible = true;
        CurrentFileText = "Indexación detenida.";
        ResultSummaryText = $"Indexación cancelada por el usuario ({processed} de {total} procesados).";
        IsResultSummaryVisible = true;
    }
}
