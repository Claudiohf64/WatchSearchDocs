namespace WatchSearchDocs;

public interface IDocumentProcessor
{
    string ProcessorName { get; }
    string SupportedCategory { get; }
    IReadOnlyList<string> SupportedExtensions { get; }
    bool CanProcess(string extension);
    Task<ProcessedDocumentResult> ProcessAsync(string filePath, CancellationToken cancellationToken = default);
}
