using System;
using System.Collections.Generic;

namespace WatchSearchDocs;

public static class DocumentClassifier
{
    private static readonly HashSet<string> PdfExtensions = new(StringComparer.OrdinalIgnoreCase) 
    { 
        ".pdf" 
    };

    private static readonly HashSet<string> WordExtensions = new(StringComparer.OrdinalIgnoreCase) 
    { 
        ".docx", ".doc" 
    };

    private static readonly HashSet<string> ExcelExtensions = new(StringComparer.OrdinalIgnoreCase) 
    { 
        ".xlsx", ".xls" 
    };

    private static readonly HashSet<string> PowerPointExtensions = new(StringComparer.OrdinalIgnoreCase) 
    { 
        ".pptx", ".ppt" 
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) 
    { 
        ".jpg", ".jpeg", ".png" 
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase) 
    { 
        ".txt" 
    };

    public static ClassificationResult Classify(string filePath, string extension)
    {
        string ext = extension.Trim().ToLowerInvariant();
        if (!ext.StartsWith('.') && !string.IsNullOrEmpty(ext))
        {
            ext = "." + ext;
        }

        if (PdfExtensions.Contains(ext))
        {
            return new ClassificationResult
            {
                IsIndexable = true,
                Category = DocumentCategory.Pdf,
                CategoryName = "Documento PDF",
                ProcessorTarget = "PdfProcessor",
                DetectedExtension = ext.ToUpperInvariant(),
                Reason = "Compatible con procesador de lectura PDF"
            };
        }

        if (WordExtensions.Contains(ext))
        {
            return new ClassificationResult
            {
                IsIndexable = true,
                Category = DocumentCategory.Word,
                CategoryName = "Documento Word",
                ProcessorTarget = "WordProcessor",
                DetectedExtension = ext.ToUpperInvariant(),
                Reason = "Compatible con procesador de documentos DOCX/DOC"
            };
        }

        if (ExcelExtensions.Contains(ext))
        {
            return new ClassificationResult
            {
                IsIndexable = true,
                Category = DocumentCategory.Excel,
                CategoryName = "Hoja de Cálculo Excel",
                ProcessorTarget = "ExcelProcessor",
                DetectedExtension = ext.ToUpperInvariant(),
                Reason = "Compatible con procesador de tablas XLSX/XLS"
            };
        }

        if (PowerPointExtensions.Contains(ext))
        {
            return new ClassificationResult
            {
                IsIndexable = true,
                Category = DocumentCategory.PowerPoint,
                CategoryName = "Presentación PowerPoint",
                ProcessorTarget = "PowerPointProcessor",
                DetectedExtension = ext.ToUpperInvariant(),
                Reason = "Compatible con procesador de presentaciones PPTX/PPT"
            };
        }

        if (ImageExtensions.Contains(ext))
        {
            return new ClassificationResult
            {
                IsIndexable = true,
                Category = DocumentCategory.Image,
                CategoryName = "Imagen (JPG / PNG)",
                ProcessorTarget = "ImageProcessor",
                DetectedExtension = ext.ToUpperInvariant(),
                Reason = "Compatible con procesador de visión y OCR de imágenes"
            };
        }

        if (TextExtensions.Contains(ext))
        {
            return new ClassificationResult
            {
                IsIndexable = true,
                Category = DocumentCategory.Text,
                CategoryName = "Texto Plano",
                ProcessorTarget = "TextProcessor",
                DetectedExtension = ext.ToUpperInvariant(),
                Reason = "Compatible con procesador de texto estructurado TXT"
            };
        }

        return new ClassificationResult
        {
            IsIndexable = false,
            Category = DocumentCategory.None,
            CategoryName = "No compatible",
            ProcessorTarget = "None",
            DetectedExtension = ext.ToUpperInvariant(),
            Reason = $"El formato '{ext}' no tiene un procesador asignado para indexación"
        };
    }
}
