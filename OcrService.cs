using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace WatchSearchDocs;

/// <summary>
/// Servicio de OCR de alto rendimiento basado en Windows.Media.Ocr nativo del sistema operativo.
/// Cero dependencias externas y optimizado con aceleración de hardware.
/// </summary>
public static class OcrService
{
    private static OcrEngine? _ocrEngine;
    private static readonly object _lock = new();

    public static bool IsOcrSupported => GetEngine() != null;

    public static OcrEngine? GetEngine()
    {
        if (_ocrEngine != null) 
            return _ocrEngine;

        lock (_lock)
        {
            if (_ocrEngine != null) 
                return _ocrEngine;

            // 1. Intentar el idioma de perfil del usuario en Windows
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();

            // 2. Si no, intentar español ("es")
            if (engine == null)
            {
                var spanishLang = OcrEngine.AvailableRecognizerLanguages
                    .FirstOrDefault(l => l.LanguageTag.StartsWith("es", StringComparison.OrdinalIgnoreCase));
                if (spanishLang != null)
                {
                    engine = OcrEngine.TryCreateFromLanguage(spanishLang);
                }
            }

            // 3. Si no, intentar inglés ("en")
            if (engine == null)
            {
                var englishLang = OcrEngine.AvailableRecognizerLanguages
                    .FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
                if (englishLang != null)
                {
                    engine = OcrEngine.TryCreateFromLanguage(englishLang);
                }
            }

            // 4. Fallback al primer idioma disponible en el sistema
            if (engine == null && OcrEngine.AvailableRecognizerLanguages.Count > 0)
            {
                engine = OcrEngine.TryCreateFromLanguage(OcrEngine.AvailableRecognizerLanguages[0]);
            }

            _ocrEngine = engine;
            return _ocrEngine;
        }
    }

    /// <summary>
    /// Reconoce texto directamente desde un flujo IRandomAccessStream (renderizado de página PDF o imagen).
    /// </summary>
    public static async Task<string> RecognizeStreamAsync(IRandomAccessStream stream)
    {
        var engine = GetEngine();
        if (engine == null)
            return string.Empty;

        try
        {
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);

            var transform = new BitmapTransform();
            uint maxDim = OcrEngine.MaxImageDimension;
            if (decoder.PixelWidth > maxDim || decoder.PixelHeight > maxDim)
            {
                double scale = (double)maxDim / Math.Max(decoder.PixelWidth, decoder.PixelHeight);
                transform.ScaledWidth = (uint)(decoder.PixelWidth * scale);
                transform.ScaledHeight = (uint)(decoder.PixelHeight * scale);
            }

            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);

            var result = await engine.RecognizeAsync(softwareBitmap);
            return result?.Text?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Extrae texto de un arreglo de bytes de imagen (PNG, JPG, BMP, TIFF) mediante Windows.Media.Ocr.
    /// </summary>
    public static async Task<string> RecognizeImageBytesAsync(byte[] imageBytes)
    {
        if (imageBytes == null || imageBytes.Length == 0)
            return string.Empty;

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(imageBytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            return await RecognizeStreamAsync(stream);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Extrae texto de un archivo de imagen en disco (.jpg, .png, etc.).
    /// </summary>
    public static async Task<string> RecognizeFileAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return string.Empty;

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(filePath);
            return await RecognizeImageBytesAsync(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }
}
