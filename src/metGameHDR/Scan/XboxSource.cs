using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Xml.Linq;

namespace MetGameHDR.Scan;

public sealed class XboxSource : IGameSource
{
    private const string Script =
        "Get-AppxPackage | ? { -not $_.IsFramework } | select Name,InstallLocation | ConvertTo-Json -Compress";

    public GameSource Source => GameSource.Xbox;

    public IEnumerable<Game> Scan()
    {
        var json = RunPowerShell();
        if (string.IsNullOrWhiteSpace(json)) yield break;

        List<JsonElement> items;
        try
        {
            using var doc = JsonDocument.Parse(json);
            items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList()
                : new List<JsonElement> { doc.RootElement.Clone() };
        }
        catch (JsonException)
        {
            yield break;
        }

        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var location = item.TryGetProperty("InstallLocation", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
            if (string.IsNullOrEmpty(location)) continue;
            var pkg = item.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";

            string xml;
            try
            {
                var config = Path.Combine(location, "MicrosoftGame.config");
                if (!File.Exists(config)) continue;
                xml = File.ReadAllText(config);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                continue;
            }
            yield return new Game(ParseDisplayName(xml, pkg), location, GameSource.Xbox, null);
        }
    }

    public static string ParseDisplayName(string configXml, string fallback)
    {
        try
        {
            var name = XDocument.Parse(configXml).Descendants()
                .Where(e => e.Name.LocalName == "ShellVisuals")
                .Select(e => (string?)e.Attribute("DefaultDisplayName"))
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
            return name ?? fallback;
        }
        catch (System.Xml.XmlException)
        {
            return fallback;
        }
    }

    private static string? RunPowerShell()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(Script);
            using var p = Process.Start(psi);
            if (p is null) return null;
            // async read: a synchronous ReadToEnd would block past the timeout
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(30000))
            {
                p.Kill(entireProcessTree: true);
                return null;
            }
            return p.ExitCode == 0 ? output.Result : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
