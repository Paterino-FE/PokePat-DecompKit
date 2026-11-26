using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace TilesetEditor.Toolkit.Install;

/// <param name="Digest">GitHub's checksum of the file ("sha256:…"), if the release has one.</param>
public sealed record ReleaseAsset(string Name, string Url, long Size, string? Digest = null);
/// <param name="Notes">The release text (markdown) – what is new.</param>
public sealed record Release(string Tag, IReadOnlyList<ReleaseAsset> Assets, string? PageUrl = null, string? Notes = null, string? Name = null)
{
    /// <summary>
    /// The version of the release: from the tag ("v0.3.0"), else the title ("PokéPat-DecompKit Version 0.3.0"), else the
    /// package name ("…-0.3.0-windows-x64.zip") – releases are not always tagged with their number.
    /// </summary>
    public string? Version =>
        new[] { Tag, Name }.Concat(Assets.Select(a => a.Name))
            .Select(t => System.Text.RegularExpressions.Regex.Match(t ?? "", @"\d+\.\d+(?:\.\d+)*"))
            .FirstOrDefault(m => m.Success)?.Value;
}

/// <summary>Latest releases and downloads from GitHub (anonymous; a generic User-Agent, nothing about the user is sent).</summary>
public static class GitHubReleases
{
    private static readonly Lazy<HttpClient> Client = new(() =>
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PokePat-DecompKit", "1.0"));
        return c;
    });

    public static HttpClient Http => Client.Value;

    /// <summary>The newest release of owner/repo, or null if GitHub cannot be reached.</summary>
    public static async Task<Release?> LatestAsync(string repo, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString();
            if (string.IsNullOrEmpty(tag)) return null;
            var assets = root.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => new ReleaseAsset(x.GetProperty("name").GetString() ?? "",
                    x.GetProperty("browser_download_url").GetString() ?? "", x.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                    x.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null)).ToList()
                : [];
            return new Release(tag, assets, root.TryGetProperty("html_url", out var h) ? h.GetString() : null,
                root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null,
                root.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) throw;
            return null;
        }
    }

    /// <summary>"expansion/1.17.2", "v6.3.1", "6.3.1.0" → comparable parts; -1/0/1 like CompareTo. Unknown text compares as equal.</summary>
    public static int CompareVersions(string? a, string? b)
    {
        static int[] Parts(string? v)
        {
            var m = System.Text.RegularExpressions.Regex.Match(v ?? "", @"(\d+(?:\.\d+)*)");
            return m.Success ? m.Groups[1].Value.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray() : [];
        }
        var x = Parts(a);
        var y = Parts(b);
        if (x.Length == 0 || y.Length == 0) return 0;
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }

    /// <summary>The version part of a tag: "expansion/1.17.2" → "1.17.2", "v6.3.1" → "6.3.1".</summary>
    public static string Short(string tag)
    {
        var m = System.Text.RegularExpressions.Regex.Match(tag, @"\d+(?:\.\d+)+");
        if (!m.Success) return tag;
        // Windows file versions have four parts: "6.3.1.0" → "6.3.1".
        return m.Value.Count(c => c == '.') == 3 && m.Value.EndsWith(".0", StringComparison.Ordinal) ? m.Value[..^2] : m.Value;
    }

    /// <summary>Downloads <paramref name="url"/> to <paramref name="file"/>, reporting "x of y MB"; returns the SHA-256 (hex, upper case).</summary>
    public static async Task<string> DownloadAsync(string url, string file, Action<string>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(file);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[81920];
        long done = 0, lastReport = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            sha.AppendData(buf, 0, n);
            done += n;
            if (done - lastReport >= 2_000_000)
            {
                lastReport = done;
                progress?.Invoke($"  {done / 1e6:0.0}" + (total is > 0 ? $" of {total.Value / 1e6:0.0}" : "") + " MB");
            }
        }
        progress?.Invoke($"  {done / 1e6:0.0} MB downloaded.");
        return Convert.ToHexString(sha.GetHashAndReset());
    }
}
