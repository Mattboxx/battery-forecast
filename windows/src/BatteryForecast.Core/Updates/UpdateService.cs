using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace BatteryForecast.Core.Updates;

/// <summary>Versione semantica minima: 1.2.3, 1.2.3-beta.2. Le prerelease precedono la versione finale.</summary>
public sealed record AppVersion(int Major, int Minor, int Patch, string Pre) : IComparable<AppVersion>
{
    public static AppVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().TrimStart('v', 'V').Split('+')[0];
        var dash = t.IndexOf('-');
        var core = (dash < 0 ? t : t[..dash]).Split('.');
        if (core.Length is < 2 or > 3 || !core.All(p => int.TryParse(p, out _))) return null;
        return new(int.Parse(core[0]), int.Parse(core[1]), core.Length > 2 ? int.Parse(core[2]) : 0, dash < 0 ? "" : t[(dash + 1)..]);
    }

    public bool IsPrerelease => Pre.Length > 0;

    public int CompareTo(AppVersion? o)
    {
        if (o is null) return 1;
        var c = (Major, Minor, Patch).CompareTo((o.Major, o.Minor, o.Patch));
        if (c != 0) return c;
        if (Pre == o.Pre) return 0;
        if (Pre.Length == 0) return 1;
        if (o.Pre.Length == 0) return -1;
        var (a, b) = (Pre.Split('.'), o.Pre.Split('.'));
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var (na, nb) = (int.TryParse(a[i], out var x), int.TryParse(b[i], out var y));
            c = na && nb ? x.CompareTo(y) : na ? -1 : nb ? 1 : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}{(Pre.Length > 0 ? "-" + Pre : "")}";
}

public sealed record UpdateInfo(AppVersion Version, string Tag, string PageUrl, string InstallerUrl, string InstallerName,
    long InstallerSize, string? ChecksumUrl, string Notes);

public sealed class UpdateException(string message) : Exception(message);

/// <summary>
/// Aggiornamenti dalle release GitHub del repository. Rete usata solo qui: una richiesta all'API pubblica,
/// senza dati personali. L'installer si scarica solo da host GitHub e si verifica con lo SHA-256 pubblicato
/// accanto (file .sha256). Le build non sono firmate: la verifica dell'hash è il controllo d'integrità.
/// </summary>
public sealed class UpdateService(HttpClient http, string repo = UpdateService.DefaultRepo)
{
    public const string DefaultRepo = "Mattboxx/battery-forecast";

    static readonly string[] AllowedHosts =
        ["github.com", "api.github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    public static HttpClient CreateClient(string version)
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BatteryForecast", version));
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return h;
    }

    public static string ArchTag => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>La release più recente più nuova di <paramref name="current"/>, o null. Prerelease solo se lo è anche la corrente.</summary>
    public async Task<UpdateInfo?> CheckAsync(AppVersion current, CancellationToken ct = default)
    {
        using var r = await http.GetAsync($"https://api.github.com/repos/{repo}/releases?per_page=20", ct).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode) throw new UpdateException($"GitHub: {(int)r.StatusCode} {r.ReasonPhrase}");
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        return Pick(doc.RootElement, current, ArchTag);
    }

    internal static UpdateInfo? Pick(JsonElement releases, AppVersion current, string arch)
    {
        UpdateInfo? best = null;
        foreach (var rel in releases.EnumerateArray())
        {
            if (rel.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
            var tag = rel.GetProperty("tag_name").GetString() ?? "";
            if (AppVersion.Parse(tag) is not { } v || v.CompareTo(current) <= 0) continue;
            if (v.IsPrerelease && !current.IsPrerelease) continue;
            var assets = rel.GetProperty("assets").EnumerateArray().ToList();
            var installer = assets.FirstOrDefault(a =>
                a.GetProperty("name").GetString() is { } n && n.EndsWith($"-{arch}.exe", StringComparison.OrdinalIgnoreCase));
            if (installer.ValueKind == JsonValueKind.Undefined) continue;
            var name = installer.GetProperty("name").GetString()!;
            var sha = assets.FirstOrDefault(a => a.GetProperty("name").GetString() == name + ".sha256");
            var info = new UpdateInfo(v, tag, rel.GetProperty("html_url").GetString() ?? "",
                installer.GetProperty("browser_download_url").GetString()!, name, installer.GetProperty("size").GetInt64(),
                sha.ValueKind == JsonValueKind.Undefined ? null : sha.GetProperty("browser_download_url").GetString(),
                rel.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "");
            if (best is null || v.CompareTo(best.Version) > 0) best = info;
        }
        return best;
    }

    internal static bool IsAllowed(Uri? uri) =>
        uri is { Scheme: "https" } && AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>"hash  nomefile" (formato di sha256sum e di tools/package.ps1).</summary>
    internal static string? ParseChecksum(string text, string fileName)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 1 && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit)
                && (parts.Length == 1 || parts[1].TrimStart('*') == fileName))
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    /// <summary>Scarica l'installer in una cartella temporanea e ne verifica l'hash. Restituisce il percorso.</summary>
    public async Task<string> DownloadAsync(UpdateInfo u, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (u.ChecksumUrl is null) throw new UpdateException("the release has no SHA-256 file");
        if (!IsAllowed(new Uri(u.InstallerUrl)) || !IsAllowed(new Uri(u.ChecksumUrl))) throw new UpdateException("unexpected download host");
        using var shaResponse = await http.GetAsync(u.ChecksumUrl, ct).ConfigureAwait(false);
        if (!shaResponse.IsSuccessStatusCode || !IsAllowed(shaResponse.RequestMessage?.RequestUri))
            throw new UpdateException("cannot read the SHA-256 file");
        var expected = ParseChecksum(await shaResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false), u.InstallerName)
            ?? throw new UpdateException("invalid SHA-256 file");

        var dir = Path.Combine(Path.GetTempPath(), "BatteryForecast-update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Path.GetFileName(u.InstallerName));
        using (var r = await http.GetAsync(u.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            if (!r.IsSuccessStatusCode || !IsAllowed(r.RequestMessage?.RequestUri)) throw new UpdateException("download failed");
            var total = r.Content.Headers.ContentLength ?? u.InstallerSize;
            await using var src = await r.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(path);
            var buffer = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        string actual;
        await using (var f = File.OpenRead(path))
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct).ConfigureAwait(false));
        if (actual != expected)
        {
            File.Delete(path);
            throw new UpdateException("SHA-256 mismatch: download discarded");
        }
        return path;
    }
}
