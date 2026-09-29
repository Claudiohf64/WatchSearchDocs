using System.Collections.ObjectModel;
using System.ComponentModel;

namespace WatchSearchDocs;

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
    public string TypeLabel => IsRoot ? "Raíz" : (IsRestricted ? "Restringido" : "Subcarpeta");
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
