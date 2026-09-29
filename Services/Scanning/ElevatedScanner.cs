using System;
using System.Collections.Generic;
using System.IO;

namespace WatchSearchDocs;

public static class ElevatedScanner
{
    public static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".vscode", ".idea",
        "node_modules", "bower_components",
        "bin", "obj", "packages", "dist", "build", "target", "out",
        ".gradle", ".cargo", "vendor",
        "$RECYCLE.BIN", "System Volume Information"
    };

    public static bool IsIgnoredDirectoryName(string dirName)
    {
        return IgnoredDirectoryNames.Contains(dirName);
    }

    public static bool IsIgnoredDirectoryPath(string fullPath)
    {
        string[] parts = fullPath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (IgnoredDirectoryNames.Contains(part))
                return true;
        }
        return false;
    }

    public static List<FileItem> ScanFolder(string targetFolder, string rootFolder)
    {
        var list = new List<FileItem>();
        var directoryInfo = new DirectoryInfo(targetFolder);

        if (!directoryInfo.Exists)
            return list;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var file in directoryInfo.EnumerateFiles("*", options))
        {
            if (IsTemporaryFile(file))
                continue;

            if (IsIgnoredDirectoryPath(file.FullName))
                continue;

            var classification = DocumentClassifier.Classify(file.FullName, file.Extension);
            if (!classification.IsIndexable)
                continue;

            string relDir = Path.GetRelativePath(rootFolder, file.DirectoryName ?? rootFolder);
            bool isRoot = string.IsNullOrEmpty(relDir) || relDir == ".";
            if (isRoot)
            {
                relDir = "Raíz";
            }

            string folderName = file.Directory?.Name ?? Path.GetFileName(rootFolder);
            string folderType = isRoot ? "Raíz" : "Subcarpeta";

            bool isHidden = IsHiddenOrSystem(file);
            bool isLocked = CheckIfFileIsLocked(file.FullName);

            list.Add(new FileItem
            {
                Name = file.Name,
                FolderName = folderName,
                FolderType = folderType,
                RelativeDirectory = relDir,
                Extension = classification.DetectedExtension,
                SizeBytes = file.Length,
                SizeFormatted = FormatFileSize(file.Length),
                LastModified = file.LastWriteTime,
                LastModifiedFormatted = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                FullPath = file.FullName,
                RootPath = rootFolder,
                IsHidden = isHidden,
                IsLocked = isLocked,
                IsIndexable = classification.IsIndexable,
                CategoryName = classification.CategoryName,
                ProcessorTarget = classification.ProcessorTarget,
                ClassificationReason = classification.Reason
            });
        }

        return list;
    }

    public static bool IsTemporaryFile(FileInfo file)
    {
        string name = file.Name;
        string ext = file.Extension.ToLowerInvariant();

        if (name.StartsWith("~$", StringComparison.OrdinalIgnoreCase))
            return true;

        if (ext == ".tmp" || ext == ".temp" || ext == ".bak")
            return true;

        if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public static bool IsHiddenOrSystem(FileInfo file)
    {
        return file.Attributes.HasFlag(FileAttributes.Hidden) ||
               file.Attributes.HasFlag(FileAttributes.System);
    }

    public static bool CheckIfFileIsLocked(string filePath)
    {
        FileStream? stream = null;
        try
        {
            stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    public static string FormatFileSize(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1 && counter < suffixes.Length - 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n1} {suffixes[counter]}";
    }
}
