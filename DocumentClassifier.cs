using System;
using System.Collections.Generic;
using System.IO;

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

/// <summary>
/// Servicio independiente encargado de la clasificación de archivos candidatos a indexación.
/// Filtra y asocia cada archivo con su procesador de documentos correspondiente.
/// Formatos soportados actuales: PDF, DOCX, XLSX, JPG, PNG, PPT, TXT.
/// </summary>
public static class DocumentClassifier
{
    // Extensiones compatibles por categoría de procesador
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

    /// <summary>
    /// Evalúa un archivo para determinar si es apto para indexación y qué procesador le corresponde.
    /// </summary>
    public static ClassificationResult Classify(string filePath, string extension)
    {
        string ext = extension.Trim().ToLowerInvariant();
        if (!ext.StartsWith('.') && !string.IsNullOrEmpty(ext))
        {
            ext = "." + ext;
        }

        // 1. Procesador PDF
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

        // 2. Procesador Word (DOCX / DOC)
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

        // 3. Procesador Excel (XLSX / XLS)
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

        // 4. Procesador PowerPoint (PPTX / PPT)
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

        // 5. Procesador de Imágenes (JPG / PNG)
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

        // 6. Procesador de Texto Plano (TXT)
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

        // 7. No compatible con los procesadores actuales
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
