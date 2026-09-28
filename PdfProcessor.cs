using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UglyToad.PdfPig;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace WatchSearchDocs;

public class PageContent
{
    public int PageNumber { get; set; }
    public string NativeText { get; set; } = string.Empty;
    public string OcrText { get; set; } = string.Empty;
    public int ImagesCount { get; set; }

    public string Text
    {
        get
        {
            if (string.IsNullOrWhiteSpace(OcrText))
                return NativeText;

            if (string.IsNullOrWhiteSpace(NativeText))
                return OcrText;

            // Si el texto OCR es mucho más completo que el nativo (catálogos, afiches publicitarios)
            // se prioriza el texto visual del OCR para evitar redundancias desordenadas.
            if (OcrText.Length > NativeText.Length * 2)
                return OcrText;

            return $"{NativeText}\r\n\r\n{OcrText}";
        }
    }

    public int CharacterCount => Text.Length;
    public int WordCount => Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

public class ProcessedDocumentResult
{
    public required string FullPath { get; set; }
    public required string FileName { get; set; }
    public string ProcessorUsed { get; set; } = "PdfProcessor (PdfPig + Windows OCR)";
    
    // Niveles: 
    // "Nivel 1: Solo Texto (PdfPig)"
    // "Nivel 2: Híbrido (Texto + Windows OCR)"
    // "Nivel 3: Solo OCR (Documento Escaneado o Gráfico)"
    public string ExtractionLevel { get; set; } = "Nivel 1: Solo Texto (PdfPig)";

    public int PageCount { get; set; }
    public int TotalCharacters { get; set; }
    public int TotalWords { get; set; }
    public int TotalImagesFound { get; set; }
    public int ImagesWithOcrText { get; set; }
    public int PagesWithOcr { get; set; }
    public int NativeTextCharacters { get; set; }
    public int OcrTextCharacters { get; set; }
    
    public string FullText { get; set; } = string.Empty;
    public List<PageContent> Pages { get; set; } = new();
    public TimeSpan ProcessingTime { get; set; }
    public double ProcessingTimeMs => ProcessingTime.TotalMilliseconds;
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime ProcessedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// Procesador de PDF de alta fidelidad:
/// 1. Extrae texto nativo estructurado por líneas y palabras mediante PdfPig.
/// 2. Si la página contiene elementos visuales, afiches o baja densidad de texto,
///    renderiza la página completa con Windows.Data.Pdf y ejecuta Windows.Media.Ocr
///    capturando el 100% de la información visible (direcciones, teléfonos, marcas, tablas).
/// </summary>
public static class PdfProcessor
{
    public static ProcessedDocumentResult ProcessPdf(string filePath)
    {
        return Task.Run(() => ProcessPdfAsync(filePath)).GetAwaiter().GetResult();
    }

    public static async Task<ProcessedDocumentResult> ProcessPdfAsync(string filePath)
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

            // 1. Abrir con PdfPig para análisis estructural y texto nativo
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var document = UglyToad.PdfPig.PdfDocument.Open(fileStream);

            int pageCount = document.NumberOfPages;
            var pagesList = new List<PageContent>();
            var fullTextBuilder = new StringBuilder();

            int totalImagesFound = 0;
            int pagesWithOcr = 0;
            int totalNativeChars = 0;
            int totalOcrChars = 0;

            // 2. Preparar el motor de renderizado nativo de Windows si se requiere OCR
            Windows.Data.Pdf.PdfDocument? winPdfDoc = null;
            try
            {
                var storageFile = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(filePath));
                winPdfDoc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(storageFile);
            }
            catch
            {
                // Si la API de StorageFile no puede acceder, continuará con extracción nativa pura
            }

            for (int i = 0; i < pageCount; i++)
            {
                var page = document.GetPage(i + 1);
                var pageContent = new PageContent { PageNumber = page.Number };

                // A. Extracción de texto nativo con ordenamiento espacial (evita palabras pegadas)
                string nativeText = ExtractCleanNativeText(page);
                pageContent.NativeText = nativeText;
                totalNativeChars += nativeText.Length;

                // B. Contar imágenes o gráficos
                var pageImages = page.GetImages().ToList();
                pageContent.ImagesCount = pageImages.Count;
                totalImagesFound += pageImages.Count;

                int nativeWords = nativeText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

                // C. Determinar si la página requiere OCR de página completa:
                // Se activa si:
                // - La página tiene imágenes o capas gráficas (catálogos, afiches, presentaciones)
                // - O la cantidad de palabras nativas es baja (< 120 palabras)
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

                // Formatear texto limpio sin comentarios artificiales
                if (!string.IsNullOrWhiteSpace(pageContent.Text))
                {
                    fullTextBuilder.AppendLine(pageContent.Text);
                    fullTextBuilder.AppendLine();
                }
            }

            sw.Stop();

            string fullText = fullTextBuilder.ToString().Trim();
            int totalWords = fullText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

            // Determinar Nivel de Extracción
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

    /// <summary>
    /// Extrae palabras de PdfPig agrupándolas por línea para evitar que palabras o números adyacentes queden pegados.
    /// </summary>
    private static string ExtractCleanNativeText(UglyToad.PdfPig.Content.Page page)
    {
        try
        {
            var words = page.GetWords().ToList();
            if (words.Count == 0)
            {
                return page.Text?.Trim() ?? string.Empty;
            }

            // Agrupar palabras que comparten la misma coordenada vertical aproximada (líneas de texto)
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

    /// <summary>
    /// Renderiza la página del PDF completa a una resolución nítida y ejecuta Windows.Media.Ocr.
    /// </summary>
    private static async Task<string> RenderAndOcrPageAsync(Windows.Data.Pdf.PdfDocument winPdfDoc, uint pageIndex)
    {
        try
        {
            using var winPage = winPdfDoc.GetPage(pageIndex);
            using var renderStream = new InMemoryRandomAccessStream();

            var renderOptions = new PdfPageRenderOptions();
            // Escalar para garantizar nitidez óptima de texto pequeño (direcciones, códigos, números)
            // Se calcula un ancho objetivo entre 1600 y 2400 píxeles según la proporción de la página
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
