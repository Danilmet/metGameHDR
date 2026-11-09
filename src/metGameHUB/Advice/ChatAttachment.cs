using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MetGameHUB.Advice;

// Block is serialized as-is into the Messages API content array.
public sealed record ChatAttachment(string Name, object Block);

public static class ChatAttachments
{
    public const int MaxImageSide = 1568;
    const int MaxImageBytes = 5 * 1024 * 1024;
    const int MaxPdfBytes = 20 * 1024 * 1024;
    const int MaxTextBytes = 1024 * 1024;
    const int MaxTextChars = 32768;

    public static ChatAttachment Prepare(string path)
    {
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var size = new FileInfo(path).Length;
        switch (ext)
        {
            case ".png": case ".jpg": case ".jpeg":
                return PrepareImage(path, name, ext == ".png" ? "image/png" : "image/jpeg", size);
            case ".gif": case ".webp":
                Limit(size, MaxImageBytes, "5 МБ");
                return Base64(name, "image", ext == ".gif" ? "image/gif" : "image/webp", File.ReadAllBytes(path));
            case ".pdf":
                Limit(size, MaxPdfBytes, "20 МБ");
                return Base64(name, "document", "application/pdf", File.ReadAllBytes(path));
            case ".txt": case ".log": case ".ini": case ".cfg": case ".json": case ".csv": case ".xml":
                Limit(size, MaxTextBytes, "1 МБ");
                return PrepareText(path, name);
            default:
                throw new AdviceException($"Тип файла не поддерживается: {ext}");
        }
    }

    public static ChatAttachment FromImage(BitmapSource image, string name) => Encode(image, name);

    static void Limit(long size, long max, string label)
    {
        if (size > max) throw new AdviceException($"Файл слишком большой (максимум {label})");
    }

    static ChatAttachment Base64(string name, string type, string media, byte[] bytes) =>
        new(name, new { type, source = new { type = "base64", media_type = media, data = Convert.ToBase64String(bytes) } });

    static ChatAttachment PrepareImage(string path, string name, string media, long size)
    {
        byte[] bytes = File.ReadAllBytes(path);
        BitmapSource frame;
        try
        {
            using var ms = new MemoryStream(bytes);
            frame = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or InvalidOperationException)
        {
            throw new AdviceException("Не удалось прочитать изображение");
        }
        if (Math.Max(frame.PixelWidth, frame.PixelHeight) <= MaxImageSide && size <= MaxImageBytes)
            return Base64(name, "image", media, bytes);
        return Encode(frame, name);
    }

    static ChatAttachment Encode(BitmapSource image, string name)
    {
        var longSide = Math.Max(image.PixelWidth, image.PixelHeight);
        if (longSide > MaxImageSide)
        {
            var k = (double)MaxImageSide / longSide;
            image = new TransformedBitmap(image, new ScaleTransform(k, k));
        }
        var png = Save(new PngBitmapEncoder(), image);
        if (png.Length <= MaxImageBytes) return Base64(name, "image", "image/png", png);
        var jpg = Save(new JpegBitmapEncoder { QualityLevel = 90 }, image);
        if (jpg.Length <= MaxImageBytes) return Base64(name, "image", "image/jpeg", jpg);
        throw new AdviceException("Изображение слишком большое (максимум 5 МБ)");
    }

    static byte[] Save(BitmapEncoder enc, BitmapSource image)
    {
        enc.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static ChatAttachment PrepareText(string path, string name)
    {
        var bytes = File.ReadAllBytes(path);
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(bytes); }
        text = ConfigFinder.MaskSecrets(text);
        if (text.Length > MaxTextChars) text = text[..MaxTextChars] + "\n[обрезано]";
        return new(name, new { type = "text", text = $"Файл {name}:\n{text}" });
    }
}
