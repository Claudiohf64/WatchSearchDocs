namespace WatchSearchDocs;

public interface IIndexService
{
    string DatabasePath { get; }
    Task EnsureDatabaseCreatedAsync();
    Task<long> GetOrCreateRootAsync(string rootPath);
    Task<long> SaveOrUpdateDocumentAsync(DocumentRecord doc);
    Task<long> IndexProcessedResultAsync(FileItem file, ProcessedDocumentResult result);
    Task<List<SearchResultItem>> SearchAsync(string query, int limit = 50);
    Task<List<IndexRootRecord>> GetAllRootsAsync();
    Task<int> GetIndexedCountAsync();
    Task<List<ProcessedDocumentResult>> GetAllIndexedResultsFromDbAsync();
}
