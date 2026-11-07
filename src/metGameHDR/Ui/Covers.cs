using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MetGameHDR.Scan;
using MetGameHDR.Steam;

namespace MetGameHDR.Ui;

public sealed class Covers(HttpClient http, string cacheDir)
{
    public static string? LocalPath(Game game, string? steamPath, VdfObject? shortcuts, string? gridDir, string cacheDir)
    {
        if (steamPath is not null && game.SteamAppId is { } id)
        {
            var lib = Path.Combine(steamPath, "appcache", "librarycache");
            foreach (var p in new[] { Path.Combine(lib, id, "library_600x900.jpg"), Path.Combine(lib, id + "_library_600x900.jpg") })
                if (File.Exists(p)) return p;
        }

        if (shortcuts is not null && gridDir is not null && Directory.Exists(gridDir)
            && ShortcutsFile.Find(shortcuts, game.InstallDir) is { } entry)
        {
            var p = Directory.EnumerateFiles(gridDir, ShortcutsFile.AppIdOf(entry) + "p.*").FirstOrDefault();
            if (p is not null) return p;
        }

        return Directory.Exists(cacheDir)
            ? Directory.EnumerateFiles(cacheDir, CacheFileName(game.InstallDir) + ".*").FirstOrDefault()
            : null;
    }

    public static string CacheFileName(string installDir)
    {
        var name = Path.GetFileName(installDir.TrimEnd('\\', '/'));
        var safe = string.Concat(name.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c));
        if (safe.Length > 60) safe = safe[..60];
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(installDir.ToUpperInvariant()));
        return safe.ToLowerInvariant() + "-" + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    static readonly ConcurrentDictionary<string, ImageSource> images = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Task<string?>> fetches = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim gate = new(3);

    public Task<string?> FetchAsync(Game game, string searchName, SteamGridDb db)
    {
        lock (fetches)
        {
            // Finished tasks stay in the map, so a game that failed is not re-queried in this process.
            if (!fetches.TryGetValue(game.InstallDir, out var task))
                fetches[game.InstallDir] = task = Task.Run(() => DownloadAsync(game, searchName, db));
            return task;
        }
    }

    async Task<string?> DownloadAsync(Game game, string searchName, SteamGridDb db)
    {
        await gate.WaitAsync();
        try
        {
            if (await db.SearchAsync(searchName) is not { } id) return null;
            if (await db.BestImageUrlAsync("grids", id, "600x900") is not { } url) return null;
            var ext = Path.GetExtension(new Uri(url).AbsolutePath);
            if (ext.Length == 0) ext = ".png";
            Directory.CreateDirectory(cacheDir);
            var path = Path.Combine(cacheDir, CacheFileName(game.InstallDir) + ext);
            await File.WriteAllBytesAsync(path, await http.GetByteArrayAsync(url));
            return path;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or JsonException)
        {
            Log.Write($"Cover {game.Name}: {e.Message}");
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    public static ImageSource? LoadImage(string path)
    {
        if (images.TryGetValue(path, out var cached)) return cached;
        try
        {
            // StreamSource, not UriSource: on a decode failure UriSource leaves the file handle open.
            using var stream = File.OpenRead(path);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = stream;
            bmp.DecodePixelWidth = 300;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return images[path] = bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static ImageSource? ExeIcon(string? exePath)
    {
        if (exePath is null || !File.Exists(exePath)) return null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon is null) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
