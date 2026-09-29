using System;
using System.Collections.Generic;

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
