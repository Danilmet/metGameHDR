using System;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace MetGameHDR.Steam;

public static class Artwork
{
    public static async Task<bool> DownloadAsync(SteamGridDb db, HttpClient http, string gridDir, uint appId, string name)
    {
        var gameId = await db.SearchAsync(name);
        if (gameId is null) return false;
        Directory.CreateDirectory(gridDir);

        var portrait = await SaveAsync(await db.BestImageUrlAsync("grids", gameId.Value, "600x900"), $"{appId}p");
        await SaveAsync(await db.BestImageUrlAsync("grids", gameId.Value, "920x430"), $"{appId}");
        await SaveAsync(await db.BestImageUrlAsync("heroes", gameId.Value, null), $"{appId}_hero");
        await SaveAsync(await db.BestImageUrlAsync("logos", gameId.Value, null), $"{appId}_logo");
        return portrait;

        async Task<bool> SaveAsync(string? url, string baseName)
        {
            if (url is null) return false;
            var ext = Path.GetExtension(new Uri(url).AbsolutePath);
            if (ext.Length == 0) ext = ".png";
            await File.WriteAllBytesAsync(Path.Combine(gridDir, baseName + ext), await http.GetByteArrayAsync(url));
            return true;
        }
    }

    public static string? SaveIcon(string exePath, string gridDir, uint appId)
    {
        using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
        if (icon is null) return null;
        Directory.CreateDirectory(gridDir);
        var path = Path.Combine(gridDir, $"{appId}_icon.png");
        using var bmp = icon.ToBitmap();
        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    public static bool HasPortrait(string gridDir, uint appId) =>
        Directory.Exists(gridDir) && Directory.EnumerateFiles(gridDir, appId + "p.*").Any();
}
