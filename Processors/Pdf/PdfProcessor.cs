using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UglyToad.PdfPig;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace WatchSearchDocs;

public class PdfProcessor : IDocumentProcessor
{
    private static readonly Lazy<PdfProcessor> _instance = new(() => new PdfProcessor());
    public static PdfProcessor Instance => _instance.Value;

    public string ProcessorName => "PdfProcessor (PdfPig + Windows OCR)";
    public string SupportedCategory => "Pdf";
    public IReadOnlyList<string> SupportedExtensions { get; } = new[] { ".pdf" };

    public bool CanProcess(string extension) =>
        !string.IsNullOrEmpty(extension) && extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public Task<ProcessedDocumentResult> ProcessAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return ProcessPdfAsync(filePath, cancellationToken);
    }

    public static ProcessedDocumentResult ProcessPdf(string filePath)
    {
        return Task.Run(() => ProcessPdfAsync(filePath)).GetAwaiter().GetResult();
    }

    public static async Task<ProcessedDocumentResult> ProcessPdfAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var fileName = Path.GetFileName(filePath);

        try
        {
            if (!File.Exists(filePath))
            {
                return new ProcessedDocumentResult
                {
                    FullPath = filePath,
                    FileName = fileName,
                    Success = false,
                    ErrorMessage = "El archivo no existe en disco.",
                    ProcessingTime = sw.Elapsed
                };
            }

            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var document = UglyToad.PdfPig.PdfDocument.Open(fileStream);

            int pageCount = document.NumberOfPages;
            var pagesList = new List<PageContent>();
            var fullTextBuilder = new StringBuilder();

            int totalImagesFound = 0;
            int pagesWithOcr = 0;
            int totalNativeChars = 0;
            int totalOcrChars = 0;

            Windows.Data.Pdf.PdfDocument? winPdfDoc = null;
            try
            {
                var storageFile = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(filePath));
                winPdfDoc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(storageFile);
            }
            catch
            {
            }

            for (int i = 0; i < pageCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var page = document.GetPage(i + 1);
                var pageContent = new PageContent { PageNumber = page.Number };

                string nativeText = ExtractCleanNativeText(page);
                pageContent.NativeText = nativeText;
                totalNativeChars += nativeText.Length;

                var pageImages = page.GetImages().ToList();
                pageContent.ImagesCount = pageImages.Count;
                totalImagesFound += pageImages.Count;

                int nativeWords = nativeText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

                bool needsFullPageOcr = winPdfDoc != null && (pageImages.Count > 0 || nativeWords < 120);

                string pageOcrText = string.Empty;

                if (needsFullPageOcr && winPdfDoc != null && i < winPdfDoc.PageCount)
                {
                    pageOcrText = await RenderAndOcrPageAsync(winPdfDoc, (uint)i);
                    if (!string.IsNullOrWhiteSpace(pageOcrText))
                    {
                        pagesWithOcr++;
                        pageContent.OcrText = pageOcrText;
                        totalOcrChars += pageOcrText.Length;
                    }
                }

                pagesList.Add(pageContent);

                if (!string.IsNullOrWhiteSpace(pageContent.Text))
                {
                    fullTextBuilder.AppendLine(pageContent.Text);
                    fullTextBuilder.AppendLine();
                }
            }

            sw.Stop();

            string fullText = fullTextBuilder.ToString().Trim();
            int totalWords = fullText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

            string level;
            if (totalNativeChars > 50 && pagesWithOcr == 0)
            {
                level = "Nivel 1: Solo Texto (PdfPig)";
            }
            else if (totalNativeChars > 50 && pagesWithOcr > 0)
            {
                level = "Nivel 2: Híbrido (Texto + Windows OCR)";
            }
            else if (pagesWithOcr > 0)
            {
                level = "Nivel 3: Solo OCR (Documento Gráfico / Escaneado)";
            }
            else
            {
                level = "Nivel 1: Solo Texto (PdfPig)";
            }

            return new ProcessedDocumentResult
            {
                FullPath = filePath,
                FileName = fileName,
                PageCount = pageCount,
                TotalCharacters = fullText.Length,
                TotalWords = totalWords,
                TotalImagesFound = totalImagesFound,
                ImagesWithOcrText = pagesWithOcr,
                PagesWithOcr = pagesWithOcr,
                NativeTextCharacters = totalNativeChars,
                OcrTextCharacters = totalOcrChars,
                ExtractionLevel = level,
                FullText = fullText,
                Pages = pagesList,
                ProcessingTime = sw.Elapsed,
                Success = true
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProcessedDocumentResult
            {
                FullPath = filePath,
                FileName = fileName,
                PageCount = 0,
                TotalCharacters = 0,
                TotalWords = 0,
                ExtractionLevel = "Fallo en extracción",
                FullText = string.Empty,
                ProcessingTime = sw.Elapsed,
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private static string ExtractCleanNativeText(UglyToad.PdfPig.Content.Page page)
    {
        try
        {
            var words = page.GetWords().ToList();
            if (words.Count == 0)
            {
                return page.Text?.Trim() ?? string.Empty;
            }

            var lines = words
                .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 5.0) * 5.0)
                .OrderByDescending(g => g.Key)
                .Select(g => string.Join(" ", g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));

            return string.Join("\r\n", lines).Trim();
        }
        catch
        {
            return page.Text?.Trim() ?? string.Empty;
        }
    }

    private static async Task<string> RenderAndOcrPageAsync(Windows.Data.Pdf.PdfDocument winPdfDoc, uint pageIndex)
    {
        try
        {
            using var winPage = winPdfDoc.GetPage(pageIndex);
            using var renderStream = new InMemoryRandomAccessStream();

            var renderOptions = new PdfPageRenderOptions();
            double scale = 2.0;
            if (winPage.Size.Width > 0)
            {
                double targetWidth = Math.Clamp(winPage.Size.Width * scale, 1500, 2400);
                renderOptions.DestinationWidth = (uint)targetWidth;
            }

            await winPage.RenderToStreamAsync(renderStream, renderOptions);
            return await OcrService.RecognizeStreamAsync(renderStream);
        }
        catch
        {
            return string.Empty;
        }
    }
}
