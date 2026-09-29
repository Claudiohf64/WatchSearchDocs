using System;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace WatchSearchDocs;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length >= 4 && e.Args[0] == "--scan-elevated")
        {
            string targetFolder = e.Args[1];
            string rootFolder = e.Args[2];
            string outputFile = e.Args[3];

            try
            {
                var files = ElevatedScanner.ScanFolder(targetFolder, rootFolder);
                var json = JsonSerializer.Serialize(files);
                File.WriteAllText(outputFile, json);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(outputFile, "ERROR: " + ex.Message);
                Environment.Exit(1);
            }
            return;
        }

        base.OnStartup(e);
    }
}
