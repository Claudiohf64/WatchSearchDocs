using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Tesseract;
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

            return $"{NativeText}\r\n\r\n--- [Texto extraído de imágenes mediante OCR] ---\r\n{OcrText}";
        }
    }

    public int CharacterCount => Text.Length;
    public int WordCount => Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

public class ProcessedDocumentResult
{
    public required string FullPath { get; set; }
    public required string FileName { get; set; }
    public string ProcessorUsed { get; set; } = "PdfProcessor (PdfPig + Tesseract)";
    
    // Niveles: 
    // "Nivel 1: Solo Texto (PdfPig)"
    // "Nivel 2: Híbrido (Texto + OCR)"
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
/// Nivel 2: Híbrido (Texto nativo PdfPig + OCR Tesseract en imágenes incrustadas).
/// Nivel 3: Solo OCR (Para páginas o documentos escaneados sin texto nativo).
/// </summary>
public static class PdfProcessor
{
    private static string? _cachedTessDataPath;

    public static ProcessedDocumentResult ProcessPdf(string filePath)
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

            // Preparar motor Tesseract bajo demanda si se encuentran imágenes
            TesseractEngine? tesseractEngine = null;
            string? tessDataPath = GetTessDataPath();

            try
            {
                foreach (UglyToad.PdfPig.Content.Page page in document.GetPages())
                {
                    var pageContent = new PageContent { PageNumber = page.Number };

                    // 1. Extraer texto nativo con PdfPig
                    string nativeText = page.Text?.Trim() ?? string.Empty;
                    pageContent.NativeText = nativeText;
                    totalNativeChars += nativeText.Length;

                    // 2. Verificar si hay imágenes en la página
                    var pageImages = page.GetImages().ToList();
                    pageContent.ImagesCount = pageImages.Count;
                    totalImagesFound += pageImages.Count;

                    var pageOcrBuilder = new StringBuilder();

                    // Si hay imágenes, inicializar Tesseract y procesar cada imagen relevante
                    if (pageImages.Count > 0 && !string.IsNullOrEmpty(tessDataPath))
                    {
                        if (tesseractEngine == null)
                        {
                            tesseractEngine = CreateTesseractEngine(tessDataPath);
                        }

                        if (tesseractEngine != null)
                        {
                            int imgIndex = 1;
                            foreach (var img in pageImages)
                            {
                                // Ignorar imágenes diminutas (espaciadores, líneas o viñetas < 40px)
                                if (img.WidthInSamples < 40 && img.HeightInSamples < 40)
                                    continue;

                                string extractedOcr = ProcessImageWithOcr(tesseractEngine, img);
                                if (!string.IsNullOrWhiteSpace(extractedOcr))
                                {
                                    imagesWithOcrText++;
                                    pageOcrBuilder.AppendLine($"[Imagen {imgIndex}]: {extractedOcr.Trim()}");
                                }
                                imgIndex++;
                            }
                        }
                    }

                    string ocrText = pageOcrBuilder.ToString().Trim();
                    pageContent.OcrText = ocrText;
                    totalOcrChars += ocrText.Length;

                    pagesList.Add(pageContent);

                    // Formatear salida para el texto global del documento
                    fullTextBuilder.AppendLine($"=== Página {page.Number} ===");
                    fullTextBuilder.AppendLine(pageContent.Text);
                    fullTextBuilder.AppendLine();
                }
            }
            finally
            {
                tesseractEngine?.Dispose();
            }

            sw.Stop();

            string fullText = fullTextBuilder.ToString().Trim();
            int totalWords = fullText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

            // Determinar los 3 Niveles de Extracción solicitados:
            string level;
            if (totalNativeChars > 30 && imagesWithOcrText == 0 && totalImagesFound == 0)
            {
                level = "Nivel 1: Solo Texto (PdfPig)";
            }
            else if (totalNativeChars > 30 && (imagesWithOcrText > 0 || totalImagesFound > 0))
            {
                level = imagesWithOcrText > 0 
                    ? "Nivel 2: Híbrido (Texto + OCR en Imágenes)" 
                    : "Nivel 1: Solo Texto (PdfPig - Imágenes sin texto)";
            }
            else if (totalNativeChars <= 30 && (imagesWithOcrText > 0 || totalImagesFound > 0))
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

    private static string ProcessImageWithOcr(TesseractEngine engine, IPdfImage pdfImage)
    {
        try
        {
            byte[]? imageBytes = null;

            if (pdfImage.TryGetPng(out byte[] pngBytes))
            {
                imageBytes = pngBytes;
            }
            else if (pdfImage.RawBytes != null && pdfImage.RawBytes.Count > 0)
            {
                imageBytes = pdfImage.RawBytes.ToArray();
            }

            if (imageBytes == null || imageBytes.Length == 0)
                return string.Empty;

            using var pix = Pix.LoadFromMemory(imageBytes);
            if (pix == null)
                return string.Empty;

            using Tesseract.Page ocrPage = engine.Process(pix);
            string ocrResult = ocrPage.GetText();

            return ocrResult?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static TesseractEngine? CreateTesseractEngine(string tessDataPath)
    {
        // Intentar español + inglés, luego español solo, luego inglés solo
        string[] languageOptions = { "spa+eng", "spa", "eng" };

        foreach (var lang in languageOptions)
        {
            try
            {
                var engine = new TesseractEngine(tessDataPath, lang, EngineMode.Default);
                return engine;
            }
            catch
            {
                // Si el archivo del idioma no está presente, probar siguiente opción
            }
        }

        return null;
    }

    private static string? GetTessDataPath()
    {
        if (_cachedTessDataPath != null && Directory.Exists(_cachedTessDataPath))
            return _cachedTessDataPath;

        string[] candidatePaths =
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata"),
            Path.Combine(Directory.GetCurrentDirectory(), "tessdata"),
            @"E:\Proyectos\INSTITUTO\WatchSearchDocs\tessdata",
            @"E:\Proyectos\INSTITUTO\WatchSearchDocs\bin\Debug\net10.0-windows\tessdata"
        };

        foreach (var path in candidatePaths)
        {
            if (Directory.Exists(path) && (File.Exists(Path.Combine(path, "eng.traineddata")) || File.Exists(Path.Combine(path, "spa.traineddata"))))
            {
                _cachedTessDataPath = path;
                return path;
            }
        }

        return null;
    }
}
