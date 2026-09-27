using System;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace WatchSearchDocs;

public class FileItem : INotifyPropertyChanged
{
    private string _indexingStatus = "Pendiente";

    public required string Name { get; set; }
    public required string FolderName { get; set; }
    public required string FolderType { get; set; } // "Raíz" o "Subcarpeta"
    public required string RelativeDirectory { get; set; }
    public required string Extension { get; set; }
    public long SizeBytes { get; set; }
    public required string SizeFormatted { get; set; }
    public DateTime LastModified { get; set; }
    public required string LastModifiedFormatted { get; set; }
    public required string FullPath { get; set; }
    public bool IsLocked { get; set; }
    public bool IsHidden { get; set; }

    // Información del Clasificador de Indexación
    public bool IsIndexable { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public string ProcessorTarget { get; set; } = string.Empty;
    public string ClassificationReason { get; set; } = string.Empty;

    // Estado del proceso de indexación: "Pendiente", "Indexado", "Error"
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

    // Propiedades visuales limpias (sin emojis)
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

public class FolderTreeNode : INotifyPropertyChanged
{
    private bool _isSelected = true;
    private bool _isExpanded = true;
    private bool _isVisible = true;

    public required string Name { get; set; }
    public required string RelativePath { get; set; }
    public required string FullPath { get; set; }
    public required string RootPath { get; set; }
    public bool IsRoot { get; set; }
    public bool IsRestricted { get; set; }
    public bool HasBeenElevated { get; set; }
    public int FileCount { get; set; }

    public string FolderType => IsRoot ? "Raíz" : "Subcarpeta";
    public string TypeLabel => IsRoot ? "[Raíz]" : (IsRestricted ? "[Restringido]" : "[Subcarpeta]");
    public string FolderColor => IsRestricted ? "#9CA3AF" : (IsRoot ? "#0F172A" : "#334155");
    public string FileCountDisplay => (IsRestricted && !HasBeenElevated) ? "Sin acceso" : $"{FileCount} arch. indexables";

    public ObservableCollection<FolderTreeNode> Subfolders { get; } = new();
    public ObservableCollection<FileItem> AllDirectFiles { get; } = new();
    public ObservableCollection<FileItem> FilteredFiles { get; } = new();

    public bool HasDirectFiles => FilteredFiles.Count > 0;
    public bool HasSubfolders => Subfolders.Count > 0;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged(nameof(IsExpanded));
            }
        }
    }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible != value)
            {
                _isVisible = value;
                OnPropertyChanged(nameof(IsVisible));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
