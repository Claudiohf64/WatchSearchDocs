using System;
using System.ComponentModel;

namespace WatchSearchDocs;

public class FileItem : INotifyPropertyChanged
{
    private string _indexingStatus = "Pendiente";

    public required string Name { get; set; }
    public required string FolderName { get; set; }
    public required string FolderType { get; set; }
    public required string RelativeDirectory { get; set; }
    public required string Extension { get; set; }
    public long SizeBytes { get; set; }
    public required string SizeFormatted { get; set; }
    public DateTime LastModified { get; set; }
    public required string LastModifiedFormatted { get; set; }
    public required string FullPath { get; set; }
    public string RootPath { get; set; } = string.Empty;
    public bool IsLocked { get; set; }
    public bool IsHidden { get; set; }

    public bool IsIndexable { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public string ProcessorTarget { get; set; } = string.Empty;
    public string ClassificationReason { get; set; } = string.Empty;

    public string IndexingStatus
    {
        get => _indexingStatus;
        set
        {
            if (_indexingStatus != value)
            {
                _indexingStatus = value;
                OnPropertyChanged(nameof(IndexingStatus));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusColor));
            }
        }
    }

    public double RowOpacity => IsHidden ? 0.5 : 1.0;
    
    public string StatusText => 
        IndexingStatus == "Indexado" ? "Indexado" :
        (IndexingStatus == "Error" ? "Error" :
        (IsLocked ? "En uso" :
        (IsHidden ? "Oculto" : "Disponible")));

    public string StatusColor => 
        IndexingStatus == "Indexado" ? "#16A34A" :
        (IndexingStatus == "Error" ? "#DC2626" :
        (IsLocked ? "#D97706" :
        (IsHidden ? "#6B7280" : "#475569")));

    public event PropertyChangedEventHandler? PropertyChanged;
    public void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
