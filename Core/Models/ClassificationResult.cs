namespace WatchSearchDocs;

public enum DocumentCategory
{
    None,
    Pdf,
    Word,
    Excel,
    PowerPoint,
    Image,
    Text
}

public class ClassificationResult
{
    public bool IsIndexable { get; set; }
    public DocumentCategory Category { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public string ProcessorTarget { get; set; } = string.Empty;
    public string DetectedExtension { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
