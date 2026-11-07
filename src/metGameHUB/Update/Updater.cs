using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace MetGameHUB.Update;

public sealed record Release(Version Version, string AssetUrl, string PageUrl);

public static class Updater
{
    public const string Repo = "Danilmet/metGameHUB";

    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("metGameHUB");
        return http;
    }

    public static Version Current => Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

#pragma warning disable IL3000 // empty Location is the signal: CoreLib is embedded only in self-contained single-file builds
    public static bool IsSelfContained => string.IsNullOrEmpty(typeof(object).Assembly.Location);
#pragma warning restore IL3000

    public static Release? Parse(string json, Version current, bool selfContained)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = Str(root, "tag_name");
            if (tag is not { Length: > 1 } || tag[0] != 'v') return null;
            if (!Version.TryParse(tag[1..], out var parsed)) return null;

            var version = Normalize(parsed);
            if (version <= Normalize(current)) return null;

            var suffix = selfContained ? "-win-x64-selfcontained.zip" : "-win-x64.zip";
            if (!root.TryGetProperty("assets", out var assets)) return null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = Str(asset, "name");
                var url = Str(asset, "browser_download_url");
                if (name is not null && url is not null && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return new Release(version, url, Str(root, "html_url") ?? "");
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task<Release?> CheckAsync()
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        response.EnsureSuccessStatusCode();
        var release = Parse(await response.Content.ReadAsStringAsync(), Current, IsSelfContained);
        Log.Write($"Update check: current {Current}, selfContained {IsSelfContained}, latest {(release is null ? "none" : "v" + release.Version.ToString(3))}");
        return release;
    }

    public static async Task<string> DownloadAsync(Release release, string updateDir)
    {
        Directory.CreateDirectory(updateDir);
        var zipPath = Path.Combine(updateDir, "update.zip");
        using (var response = await Http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var src = await response.Content.ReadAsStreamAsync();
            await using var dst = new FileStream(zipPath, FileMode.Create);
            await src.CopyToAsync(dst);
        }
        return await Task.Run(() => Extract(zipPath, Path.Combine(updateDir, "unpacked")));
    }

    public static string Extract(string zipPath, string dir)
    {
        ZipFile.ExtractToDirectory(zipPath, dir, overwriteFiles: true);
        var found = Directory.GetFiles(dir, "metGameHUB.exe", SearchOption.AllDirectories);
        return found.Length > 0 ? found[0] : throw new FileNotFoundException($"metGameHUB.exe not found in {zipPath}", zipPath);
    }

    public static void Swap(string exe, string newExe)
    {
        var old = exe + ".old";
        File.Move(exe, old, overwrite: true);
        try
        {
            File.Move(newExe, exe);
        }
        catch
        {
            File.Move(old, exe, overwrite: true);
            throw;
        }
    }

    public static void Cleanup(string exe, string updateDir)
    {
        // Ignored: a file still locked by the old process is harmless and is removed on the next start.
        try { File.Delete(exe + ".old"); } catch (Exception) { }
        try { Directory.Delete(updateDir, recursive: true); } catch (Exception) { }
    }

    // Build is -1 when the tag has fewer than three components (e.g. "v1.2").
    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
