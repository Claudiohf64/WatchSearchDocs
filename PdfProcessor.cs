using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

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
    // "Nivel 3: Solo OCR (Documento Escaneado)"
    public string ExtractionLevel { get; set; } = "Nivel 1: Solo Texto (PdfPig)";

    public int PageCount { get; set; }
    public int TotalCharacters { get; set; }
    public int TotalWords { get; set; }
    public int TotalImagesFound { get; set; }
    public int ImagesWithOcrText { get; set; }
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
/// Procesador inteligente de PDF con 3 niveles de extracción:
/// Nivel 1: Solo Texto Nativo (PdfPig).
/// Nivel 2: Híbrido (Texto nativo PdfPig + OCR nativo de Windows en imágenes incrustadas).
/// Nivel 3: Solo OCR (Para páginas o documentos escaneados sin texto nativo).
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

            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var document = PdfDocument.Open(fileStream);

            int pageCount = document.NumberOfPages;
            var pagesList = new List<PageContent>();
            var fullTextBuilder = new StringBuilder();

            int totalImagesFound = 0;
            int imagesWithOcrText = 0;
            int totalNativeChars = 0;
            int totalOcrChars = 0;

            foreach (UglyToad.PdfPig.Content.Page page in document.GetPages())
            {
                var pageContent = new PageContent { PageNumber = page.Number };

                // 1. Extraer texto nativo con PdfPig
                string nativeText = page.Text?.Trim() ?? string.Empty;
                pageContent.NativeText = nativeText;
                totalNativeChars += nativeText.Length;

                // 2. Analizar imágenes en la página
                var pageImages = page.GetImages().ToList();
                pageContent.ImagesCount = pageImages.Count;
                totalImagesFound += pageImages.Count;

                var pageOcrBuilder = new StringBuilder();

                // Optimización inteligente de OCR:
                // Si la página ya tiene más de 50 palabras nativas (aproximadamente 250 caracteres),
                // el documento es digital y las imágenes suelen ser logotipos o diagramas decorativos.
                // Solo se ejecuta OCR en imágenes si el texto nativo es escaso o nulo (< 50 palabras).
                int nativeWordCount = nativeText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                bool needsImageOcr = nativeWordCount < 50 && pageImages.Count > 0;

                if (needsImageOcr)
                {
                    int ocrCandidatesProcessed = 0;

                    foreach (var img in pageImages)
                    {
                        // Limitar a máximo 2 imágenes principales por página para optimizar velocidad
                        if (ocrCandidatesProcessed >= 2)
                            break;

                        // Descartar imágenes pequeñas (íconos, firmas, logos decorativos < 150x150 píxeles)
                        if (img.WidthInSamples < 150 || img.HeightInSamples < 150)
                            continue;

                        byte[]? imageBytes = ExtractImageBytes(img);
                        if (imageBytes != null && imageBytes.Length > 0)
                        {
                            string ocrTextResult = await OcrService.RecognizeImageBytesAsync(imageBytes);
                            if (!string.IsNullOrWhiteSpace(ocrTextResult))
                            {
                                imagesWithOcrText++;
                                pageOcrBuilder.AppendLine(ocrTextResult.Trim());
                            }
                            ocrCandidatesProcessed++;
                        }
                    }
                }

                string ocrText = pageOcrBuilder.ToString().Trim();
                pageContent.OcrText = ocrText;
                totalOcrChars += ocrText.Length;

                pagesList.Add(pageContent);

                // Formatear salida para el texto global del documento sin comentarios artificiales
                if (!string.IsNullOrWhiteSpace(pageContent.Text))
                {
                    fullTextBuilder.AppendLine(pageContent.Text);
                    fullTextBuilder.AppendLine();
                }
            }

            sw.Stop();

            string fullText = fullTextBuilder.ToString().Trim();
            int totalWords = fullText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

            // Determinar los 3 Niveles de Extracción:
            string level;
            if (totalNativeChars > 30 && imagesWithOcrText == 0)
            {
                level = "Nivel 1: Solo Texto (PdfPig)";
            }
            else if (totalNativeChars > 30 && imagesWithOcrText > 0)
            {
                level = "Nivel 2: Híbrido (Texto + Windows OCR)";
            }
            else if (totalNativeChars <= 30 && imagesWithOcrText > 0)
            {
                level = "Nivel 3: Solo OCR (Documento Escaneado)";
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
                ImagesWithOcrText = imagesWithOcrText,
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

    private static byte[]? ExtractImageBytes(IPdfImage pdfImage)
    {
        try
        {
            if (pdfImage.TryGetPng(out byte[] pngBytes))
                return pngBytes;

            if (pdfImage.RawBytes != null && pdfImage.RawBytes.Count > 0)
                return pdfImage.RawBytes.ToArray();

            return null;
        }
        catch
        {
            return null;
        }
    }
}
