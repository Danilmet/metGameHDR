using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MetGameHUB.Advice;
using Xunit;

namespace MetGameHUB.Tests;

public class ChatAttachmentTests : IDisposable
{
    readonly string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "chat" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(dir, true);

    static void Sta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception e) { error = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Exception(error.Message, error);
    }

    static byte[] Encode(int w, int h, BitmapEncoder enc)
    {
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i++) px[i] = (byte)(i * 31);
        bmp.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), px, w * 4, 0);
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static JsonElement Json(ChatAttachment a) => JsonDocument.Parse(JsonSerializer.Serialize(a.Block)).RootElement;

    [Fact]
    public void Prepare_LargePng_DownscaledTo1568() => Sta(() =>
    {
        var path = Path.Combine(dir, "a.png");
        File.WriteAllBytes(path, Encode(3136, 1000, new PngBitmapEncoder()));
        var src = Json(ChatAttachments.Prepare(path)).GetProperty("source");
        Assert.Equal("image", Json(ChatAttachments.Prepare(path)).GetProperty("type").GetString());
        Assert.Equal("image/png", src.GetProperty("media_type").GetString());
        var img = BitmapFrame.Create(new MemoryStream(Convert.FromBase64String(src.GetProperty("data").GetString()!)));
        Assert.Equal(1568, img.PixelWidth);
        Assert.Equal(500, img.PixelHeight);
    });

    [Fact]
    public void Prepare_SmallJpg_KeptAsJpeg() => Sta(() =>
    {
        var path = Path.Combine(dir, "a.jpg");
        var bytes = Encode(100, 100, new JpegBitmapEncoder());
        File.WriteAllBytes(path, bytes);
        var src = Json(ChatAttachments.Prepare(path)).GetProperty("source");
        Assert.Equal("image/jpeg", src.GetProperty("media_type").GetString());
        Assert.Equal(bytes, Convert.FromBase64String(src.GetProperty("data").GetString()!));
    });

    [Fact]
    public void Prepare_Pdf_DocumentBlock()
    {
        var path = Path.Combine(dir, "a.pdf");
        var bytes = "%PDF-1.4"u8.ToArray();
        File.WriteAllBytes(path, bytes);
        var j = Json(ChatAttachments.Prepare(path));
        Assert.Equal("document", j.GetProperty("type").GetString());
        Assert.Equal("application/pdf", j.GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal(Convert.ToBase64String(bytes), j.GetProperty("source").GetProperty("data").GetString());
    }

    [Fact]
    public void Prepare_TextFile_MaskedAndTruncated()
    {
        var path = Path.Combine(dir, "a.log");
        File.WriteAllText(path, "password=hunter2\n" + new string('x', 40000));
        var j = Json(ChatAttachments.Prepare(path));
        var text = j.GetProperty("text").GetString()!;
        Assert.Equal("text", j.GetProperty("type").GetString());
        Assert.StartsWith("Файл a.log:\n", text);
        Assert.Contains("password=***", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.True(text.Length <= "Файл a.log:\n".Length + 32768 + "\n[обрезано]".Length);
    }

    [Fact]
    public void Prepare_UnsupportedOrTooLarge_Throws()
    {
        var exe = Path.Combine(dir, "a.exe");
        File.WriteAllBytes(exe, [1]);
        Assert.Contains("Тип файла не поддерживается", Assert.Throws<AdviceException>(() => ChatAttachments.Prepare(exe)).Message);
        var log = Path.Combine(dir, "big.log");
        File.WriteAllBytes(log, new byte[1024 * 1024 + 1]);
        Assert.Throws<AdviceException>(() => ChatAttachments.Prepare(log));
    }
}
