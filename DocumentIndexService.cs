using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace WatchSearchDocs;

/// <summary>
/// Servicio de persistencia y búsqueda indexada en SQLite con soporte para FTS5.
/// Administra las tablas IndexRoots, Documents y la tabla virtual DocumentsFTS.
/// </summary>
public class DocumentIndexService
{
    private static readonly Lazy<DocumentIndexService> _instance = new(() => new DocumentIndexService());
    public static DocumentIndexService Instance => _instance.Value;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private bool _isInitialized;

    public string DatabasePath { get; }

    public DocumentIndexService(string? customDbPath = null)
    {
        DatabasePath = customDbPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WatchSearchDocs.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    /// <summary>
    /// Normaliza una ruta a minúsculas y sin separadores finales para garantizar unicidad.
    /// </summary>
    public static string NormalizePathKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        return Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant();
    }

    /// <summary>
    /// Inicializa la base de datos creando las tablas IndexRoots, Documents,
    /// la tabla virtual DocumentsFTS (FTS5) y los disparadores automáticos de sincronización.
    /// </summary>
    public async Task EnsureDatabaseCreatedAsync()
    {
        if (_isInitialized) return;

        await _dbLock.WaitAsync();
        try
        {
            if (_isInitialized) return;

            var dir = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            // Optimización de rendimiento SQLite
            await using (var pragmaCmd = connection.CreateCommand())
            {
                pragmaCmd.CommandText = @"
                    PRAGMA foreign_keys = ON;
                    PRAGMA journal_mode = WAL;
                    PRAGMA synchronous = NORMAL;
                ";
                await pragmaCmd.ExecuteNonQueryAsync();
            }

            // Creación de tablas estructuradas
            await using (var createCmd = connection.CreateCommand())
            {
                createCmd.CommandText = @"
                    -- 1. Tabla de Carpetas Raíz
                    CREATE TABLE IF NOT EXISTS IndexRoots (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        RootPath TEXT NOT NULL,
                        PathKey TEXT NOT NULL UNIQUE,
                        IsActive INTEGER NOT NULL DEFAULT 1,
                        AddedAtUtc TEXT NOT NULL
                    );

                    -- 2. Tabla Principal de Documentos
                    CREATE TABLE IF NOT EXISTS Documents (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        RootId INTEGER NOT NULL,
                        FullPath TEXT NOT NULL,
                        PathKey TEXT NOT NULL UNIQUE,
                        FileName TEXT NOT NULL,
                        Extension TEXT NOT NULL,
                        Category TEXT NOT NULL,
                        SizeBytes INTEGER NOT NULL,
                        LastModifiedUtc TEXT NOT NULL,
                        IsHidden INTEGER NOT NULL DEFAULT 0,
                        IsReadOnly INTEGER NOT NULL DEFAULT 0,
                        Content TEXT,
                        PageCount INTEGER,
                        OcrUsed INTEGER NOT NULL DEFAULT 0,
                        ProcessingStatus TEXT NOT NULL,
                        ProcessingError TEXT,
                        IndexedAtUtc TEXT NOT NULL,
                        FOREIGN KEY (RootId) REFERENCES IndexRoots(Id) ON DELETE CASCADE
                    );

                    CREATE INDEX IF NOT EXISTS idx_documents_rootid ON Documents(RootId);
                    CREATE INDEX IF NOT EXISTS idx_documents_category ON Documents(Category);
                    CREATE INDEX IF NOT EXISTS idx_documents_status ON Documents(ProcessingStatus);
                    CREATE INDEX IF NOT EXISTS idx_documents_pathkey ON Documents(PathKey);

                    -- 3. Tabla Virtual FTS5 para Búsqueda Textual Rápida
                    CREATE VIRTUAL TABLE IF NOT EXISTS DocumentsFTS USING fts5(
                        FileName,
                        Content,
                        content='Documents',
                        content_rowid='Id',
                        tokenize='unicode61 remove_diacritics 2'
                    );

                    -- Disparador: Sincronizar inserciones hacia DocumentsFTS
                    CREATE TRIGGER IF NOT EXISTS trg_documents_ai AFTER INSERT ON Documents BEGIN
                        INSERT INTO DocumentsFTS(rowid, FileName, Content) VALUES (new.Id, new.FileName, new.Content);
                    END;

                    -- Disparador: Sincronizar eliminaciones hacia DocumentsFTS
                    CREATE TRIGGER IF NOT EXISTS trg_documents_ad AFTER DELETE ON Documents BEGIN
                        INSERT INTO DocumentsFTS(DocumentsFTS, rowid, FileName, Content) VALUES ('delete', old.Id, old.FileName, old.Content);
                    END;

                    -- Disparador: Sincronizar actualizaciones hacia DocumentsFTS
                    CREATE TRIGGER IF NOT EXISTS trg_documents_au AFTER UPDATE ON Documents BEGIN
                        INSERT INTO DocumentsFTS(DocumentsFTS, rowid, FileName, Content) VALUES ('delete', old.Id, old.FileName, old.Content);
                        INSERT INTO DocumentsFTS(rowid, FileName, Content) VALUES (new.Id, new.FileName, new.Content);
                    END;
                ";
                await createCmd.ExecuteNonQueryAsync();
            }

            _isInitialized = true;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// Obtiene o registra una carpeta raíz en IndexRoots y retorna su Id.
    /// </summary>
    public async Task<long> GetOrCreateRootAsync(string rootPath)
    {
        await EnsureDatabaseCreatedAsync();

        string pathKey = NormalizePathKey(rootPath);
        string normalizedRootPath = Path.GetFullPath(rootPath);

        await _dbLock.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            // Buscar si ya existe
            await using (var selectCmd = connection.CreateCommand())
            {
                selectCmd.CommandText = "SELECT Id FROM IndexRoots WHERE PathKey = @PathKey LIMIT 1;";
                selectCmd.Parameters.AddWithValue("@PathKey", pathKey);
                var existingId = await selectCmd.ExecuteScalarAsync();
                if (existingId != null && existingId != DBNull.Value)
                {
                    return Convert.ToInt64(existingId);
                }
            }

            // Insertar nueva raíz
            await using (var insertCmd = connection.CreateCommand())
            {
                insertCmd.CommandText = @"
                    INSERT INTO IndexRoots (RootPath, PathKey, IsActive, AddedAtUtc)
                    VALUES (@RootPath, @PathKey, 1, @AddedAtUtc);
                    SELECT last_insert_rowid();
                ";
                insertCmd.Parameters.AddWithValue("@RootPath", normalizedRootPath);
                insertCmd.Parameters.AddWithValue("@PathKey", pathKey);
                insertCmd.Parameters.AddWithValue("@AddedAtUtc", DateTime.UtcNow.ToString("o"));

                var newId = await insertCmd.ExecuteScalarAsync();
                return Convert.ToInt64(newId);
            }
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// Guarda o actualiza un documento en la tabla Documents. FTS5 se actualiza automáticamente por disparadores.
    /// </summary>
    public async Task<long> SaveOrUpdateDocumentAsync(DocumentRecord doc)
    {
        await EnsureDatabaseCreatedAsync();

        if (string.IsNullOrWhiteSpace(doc.PathKey))
        {
            doc.PathKey = NormalizePathKey(doc.FullPath);
        }

        if (string.IsNullOrWhiteSpace(doc.IndexedAtUtc))
        {
            doc.IndexedAtUtc = DateTime.UtcNow.ToString("o");
        }

        await _dbLock.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Documents (
                    RootId, FullPath, PathKey, FileName, Extension, Category,
                    SizeBytes, LastModifiedUtc, IsHidden, IsReadOnly,
                    Content, PageCount, OcrUsed, ProcessingStatus, ProcessingError, IndexedAtUtc
                ) VALUES (
                    @RootId, @FullPath, @PathKey, @FileName, @Extension, @Category,
                    @SizeBytes, @LastModifiedUtc, @IsHidden, @IsReadOnly,
                    @Content, @PageCount, @OcrUsed, @ProcessingStatus, @ProcessingError, @IndexedAtUtc
                )
                ON CONFLICT(PathKey) DO UPDATE SET
                    RootId = excluded.RootId,
                    FullPath = excluded.FullPath,
                    FileName = excluded.FileName,
                    Extension = excluded.Extension,
                    Category = excluded.Category,
                    SizeBytes = excluded.SizeBytes,
                    LastModifiedUtc = excluded.LastModifiedUtc,
                    IsHidden = excluded.IsHidden,
                    IsReadOnly = excluded.IsReadOnly,
                    Content = excluded.Content,
                    PageCount = excluded.PageCount,
                    OcrUsed = excluded.OcrUsed,
                    ProcessingStatus = excluded.ProcessingStatus,
                    ProcessingError = excluded.ProcessingError,
                    IndexedAtUtc = excluded.IndexedAtUtc;

                SELECT Id FROM Documents WHERE PathKey = @PathKey LIMIT 1;
            ";

            cmd.Parameters.AddWithValue("@RootId", doc.RootId);
            cmd.Parameters.AddWithValue("@FullPath", doc.FullPath);
            cmd.Parameters.AddWithValue("@PathKey", doc.PathKey);
            cmd.Parameters.AddWithValue("@FileName", doc.FileName);
            cmd.Parameters.AddWithValue("@Extension", doc.Extension);
            cmd.Parameters.AddWithValue("@Category", doc.Category);
            cmd.Parameters.AddWithValue("@SizeBytes", doc.SizeBytes);
            cmd.Parameters.AddWithValue("@LastModifiedUtc", doc.LastModifiedUtc);
            cmd.Parameters.AddWithValue("@IsHidden", doc.IsHidden);
            cmd.Parameters.AddWithValue("@IsReadOnly", doc.IsReadOnly);
            cmd.Parameters.AddWithValue("@Content", (object?)doc.Content ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@PageCount", (object?)doc.PageCount ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@OcrUsed", doc.OcrUsed);
            cmd.Parameters.AddWithValue("@ProcessingStatus", doc.ProcessingStatus);
            cmd.Parameters.AddWithValue("@ProcessingError", (object?)doc.ProcessingError ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@IndexedAtUtc", doc.IndexedAtUtc);

            var idResult = await cmd.ExecuteScalarAsync();
            long documentId = Convert.ToInt64(idResult);
            doc.Id = documentId;
            return documentId;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// Toma el resultado procesado de un archivo y lo persiste directamente en SQLite.
    /// </summary>
    public async Task<long> IndexProcessedResultAsync(FileItem file, ProcessedDocumentResult result)
    {
        string rootPath = !string.IsNullOrWhiteSpace(file.RootPath) 
            ? file.RootPath 
            : (Path.GetDirectoryName(file.FullPath) ?? file.FullPath);

        long rootId = await GetOrCreateRootAsync(rootPath);

        bool isReadOnly = false;
        try
        {
            if (File.Exists(file.FullPath))
            {
                var fi = new FileInfo(file.FullPath);
                isReadOnly = fi.IsReadOnly;
            }
        }
        catch { }

        int ocrUsed = (result.PagesWithOcr > 0 || result.ExtractionLevel.Contains("OCR", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
        string status = result.Success ? "Indexed" : "Error";

        var record = new DocumentRecord
        {
            RootId = rootId,
            FullPath = file.FullPath,
            PathKey = NormalizePathKey(file.FullPath),
            FileName = file.Name,
            Extension = file.Extension,
            Category = !string.IsNullOrWhiteSpace(file.CategoryName) ? file.CategoryName : "Pdf",
            SizeBytes = file.SizeBytes,
            LastModifiedUtc = file.LastModified.ToUniversalTime().ToString("o"),
            IsHidden = file.IsHidden ? 1 : 0,
            IsReadOnly = isReadOnly ? 1 : 0,
            Content = result.FullText,
            PageCount = result.PageCount,
            OcrUsed = ocrUsed,
            ProcessingStatus = status,
            ProcessingError = result.ErrorMessage,
            IndexedAtUtc = DateTime.UtcNow.ToString("o")
        };

        return await SaveOrUpdateDocumentAsync(record);
    }

    /// <summary>
    /// Ejecuta una búsqueda rápida mediante FTS5 sobre el nombre y contenido de los documentos indexados.
    /// Retorna resultados ordenados por relevancia (bm25) con fragmentos de texto resaltados.
    /// </summary>
    public async Task<List<SearchResultItem>> SearchAsync(string query, int limit = 50)
    {
        var results = new List<SearchResultItem>();
        if (string.IsNullOrWhiteSpace(query))
            return results;

        await EnsureDatabaseCreatedAsync();

        // Preparar término para FTS5 (eliminar comillas sueltas o caracteres especiales para evitar errores de sintaxis)
        string sanitizedQuery = query.Trim().Replace("\"", "\"\"");
        string matchExpression = $"\"{sanitizedQuery}\"*";

        await _dbLock.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT d.Id, d.RootId, d.FullPath, d.FileName, d.Extension, d.Category,
                       d.SizeBytes, d.LastModifiedUtc, d.PageCount, d.OcrUsed,
                       snippet(DocumentsFTS, 1, '<b>', '</b>', '...', 20) AS SnippetText,
                       bm25(DocumentsFTS) AS Rank
                FROM DocumentsFTS fts
                JOIN Documents d ON d.Id = fts.rowid
                WHERE DocumentsFTS MATCH @Query
                ORDER BY Rank
                LIMIT @Limit;
            ";

            cmd.Parameters.AddWithValue("@Query", matchExpression);
            cmd.Parameters.AddWithValue("@Limit", limit);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new SearchResultItem
                {
                    Id = reader.GetInt64(0),
                    RootId = reader.GetInt64(1),
                    FullPath = reader.GetString(2),
                    FileName = reader.GetString(3),
                    Extension = reader.GetString(4),
                    Category = reader.GetString(5),
                    SizeBytes = reader.GetInt64(6),
                    LastModifiedUtc = reader.GetString(7),
                    PageCount = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    OcrUsed = reader.GetInt32(9),
                    Snippet = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
                    Rank = reader.GetDouble(11)
                });
            }
        }
        catch
        {
            // Fallback en caso de sintaxis FTS5 compleja
        }
        finally
        {
            _dbLock.Release();
        }

        return results;
    }

    /// <summary>
    /// Retorna todas las raíces actualmente registradas en IndexRoots.
    /// </summary>
    public async Task<List<IndexRootRecord>> GetAllRootsAsync()
    {
        await EnsureDatabaseCreatedAsync();
        var roots = new List<IndexRootRecord>();

        await _dbLock.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Id, RootPath, PathKey, IsActive, AddedAtUtc FROM IndexRoots ORDER BY Id ASC;";

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                roots.Add(new IndexRootRecord
                {
                    Id = reader.GetInt64(0),
                    RootPath = reader.GetString(1),
                    PathKey = reader.GetString(2),
                    IsActive = reader.GetInt32(3) == 1,
                    AddedAtUtc = reader.GetString(4)
                });
            }
        }
        finally
        {
            _dbLock.Release();
        }

        return roots;
    }

    /// <summary>
    /// Retorna la cantidad total de documentos indexados en Documents.
    /// </summary>
    public async Task<int> GetIndexedCountAsync()
    {
        await EnsureDatabaseCreatedAsync();

        await _dbLock.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Documents WHERE ProcessingStatus = 'Indexed';";
            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// Recupera todos los documentos indexados desde la base de datos SQLite para hidratar la interfaz de usuario.
    /// </summary>
    public async Task<List<ProcessedDocumentResult>> GetAllIndexedResultsFromDbAsync()
    {
        await EnsureDatabaseCreatedAsync();
        var results = new List<ProcessedDocumentResult>();

        await _dbLock.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT FullPath, FileName, Content, PageCount, OcrUsed, ProcessingStatus, ProcessingError, IndexedAtUtc, SizeBytes
                FROM Documents
                WHERE ProcessingStatus = 'Indexed'
                ORDER BY Id DESC;
            ";

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                string fullPath = reader.GetString(0);
                string fileName = reader.GetString(1);
                string content = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                int pageCount = reader.IsDBNull(3) ? 1 : reader.GetInt32(3);
                int ocrUsed = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
                string status = reader.GetString(5);
                string? error = reader.IsDBNull(6) ? null : reader.GetString(6);
                string indexedAt = reader.GetString(7);

                int totalWords = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

                var pagesList = new List<PageContent>();
                var rawPages = content.Split(new[] { "=== Página " }, StringSplitOptions.RemoveEmptyEntries);
                if (rawPages.Length > 1)
                {
                    int pNum = 1;
                    foreach (var rawPage in rawPages)
                    {
                        int endHeaderIdx = rawPage.IndexOf("===");
                        string pageBody = endHeaderIdx >= 0 ? rawPage.Substring(endHeaderIdx + 3).Trim() : rawPage.Trim();
                        pagesList.Add(new PageContent
                        {
                            PageNumber = pNum++,
                            NativeText = pageBody
                        });
                    }
                }
                else
                {
                    pagesList.Add(new PageContent
                    {
                        PageNumber = 1,
                        NativeText = content
                    });
                }

                results.Add(new ProcessedDocumentResult
                {
                    FullPath = fullPath,
                    FileName = fileName,
                    FullText = content,
                    PageCount = Math.Max(pageCount, pagesList.Count),
                    TotalCharacters = content.Length,
                    TotalWords = totalWords,
                    ExtractionLevel = ocrUsed == 1 ? "Nivel 2: Híbrido (Recuperado de BD)" : "Nivel 1: Solo Texto (Recuperado de BD)",
                    Pages = pagesList,
                    Success = status == "Indexed",
                    ErrorMessage = error,
                    ProcessedAt = DateTime.TryParse(indexedAt, out var dt) ? dt : DateTime.Now
                });
            }
        }
        finally
        {
            _dbLock.Release();
        }

        return results;
    }
}
