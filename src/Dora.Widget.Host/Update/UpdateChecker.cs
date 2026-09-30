using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Dora.Widget.Host.Update;

internal sealed record UpdateAsset(string Name, string BrowserDownloadUrl, long Size);

internal sealed record UpdateRelease(string TagName, string HtmlUrl, IReadOnlyList<UpdateAsset> Assets)
{
    public UpdateAsset? FindZip() => Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    /// <summary>Single exe used for in-place replacement; fixed name, see SELF_UPDATE_RELEASE_GUIDE.md.</summary>
    public UpdateAsset? FindExe(string exeName) => Assets.FirstOrDefault(a => a.Name.Equals(exeName, StringComparison.OrdinalIgnoreCase));
    public UpdateAsset? FindChecksums() => Assets.FirstOrDefault(a => a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Checks GitHub Releases for a newer version and downloads/verifies its assets. Offline or failing calls yield null.</summary>
internal static class UpdateChecker
{
    private const string ApiUrl = "https://api.github.com/repos/leedoha0403/ModuleDocker/releases/latest";

    private static readonly HttpClient Client = Create();

    private static HttpClient Create()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ModuleDock-UpdateChecker/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static async Task<UpdateRelease?> FetchLatestAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client.GetAsync(ApiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            if (tag.Length == 0) return null;
            var htmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";

            var assets = new List<UpdateAsset>();
            if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in arr.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                    var size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    if (name.Length > 0 && url.Length > 0) assets.Add(new UpdateAsset(name, url, size));
                }
            }

            return new UpdateRelease(tag, htmlUrl, assets);
        }
        catch
        {
            return null;
        }
    }

    public static bool IsNewer(string latestTag, string currentVersion)
    {
        var latest = ParseVersion(latestTag);
        var current = ParseVersion(currentVersion);
        return latest is not null && current is not null && latest > current;
    }

    internal static Version? ParseVersion(string text)
    {
        // .NET appends "+<git-sha>" to InformationalVersion and local builds carry "-internal": the numeric
        // version is always the part before the first '-' or '+'.
        var s = text.TrimStart('v', 'V');
        var cut = s.IndexOfAny(['-', '+']);
        if (cut >= 0) s = s[..cut];
        return Version.TryParse(s, out var v) ? v : null;
    }

    public static async Task<string> DownloadAssetAsync(UpdateAsset asset, string destinationDirectory,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        var path = Path.Combine(destinationDirectory, asset.Name);

        using var response = await Client.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? asset.Size;

        await using var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using (var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long readTotal = 0;
            int read;
            while ((read = await httpStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                readTotal += read;
                if (total > 0) progress?.Report((double)readTotal / total);
            }
        }
        return path;
    }

    public static async Task<string?> FetchTextAsync(string url, CancellationToken cancellationToken)
    {
        try { return await Client.GetStringAsync(url, cancellationToken); }
        catch { return null; }
    }

    /// <summary>
    /// False when sumsText lists fileName with a different hash. A missing line passes unless requireEntry is set;
    /// an exe that is about to be run is always verified with requireEntry.
    /// </summary>
    public static bool VerifyChecksum(string sumsText, string fileName, string filePath, bool requireEntry = false)
    {
        var expected = sumsText
            .Split('\n')
            .Select(l => l.Trim())
            .Select(l => l.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(parts => parts.Length == 2 && parts[1].TrimStart('*').Equals(fileName, StringComparison.OrdinalIgnoreCase))
            ?.FirstOrDefault();
        if (expected is null) return !requireEntry;

        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = Convert.ToHexString(sha256.ComputeHash(stream));
        return hash.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
