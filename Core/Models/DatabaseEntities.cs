namespace WatchSearchDocs;

public class IndexRootRecord
{
    public long Id { get; set; }
    public string RootPath { get; set; } = string.Empty;
    public string PathKey { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public string AddedAtUtc { get; set; } = string.Empty;
}

public class DocumentRecord
{
    public long Id { get; set; }
    public long RootId { get; set; }
    public string FullPath { get; set; } = string.Empty;
    public string PathKey { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string LastModifiedUtc { get; set; } = string.Empty;
    public int IsHidden { get; set; }
    public int IsReadOnly { get; set; }
    public string? Content { get; set; }
    public int? PageCount { get; set; }
    public int OcrUsed { get; set; }
    public string ProcessingStatus { get; set; } = "Indexed";
    public string? ProcessingError { get; set; }
    public string IndexedAtUtc { get; set; } = string.Empty;
}

public class SearchResultItem
{
    public long Id { get; set; }
    public long RootId { get; set; }
    public string FullPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string LastModifiedUtc { get; set; } = string.Empty;
    public int? PageCount { get; set; }
    public int OcrUsed { get; set; }
    public string Snippet { get; set; } = string.Empty;
    public double Rank { get; set; }
}
