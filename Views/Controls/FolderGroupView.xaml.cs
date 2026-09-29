using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WatchSearchDocs;

public partial class FolderGroupView : UserControl
{
    public FolderGroupView()
    {
        InitializeComponent();
    }

    private void LvFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListView lv && lv.SelectedItem is FileItem selectedFile)
        {
            try
            {
                Process.Start(new ProcessStartInfo(selectedFile.FullPath)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"No se pudo abrir el archivo:\n{ex.Message}", "Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}
