using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BatteryForecast.Core.Updates;

namespace BatteryForecast.Tests;

public class UpdateTests
{
    static AppVersion V(string s) => AppVersion.Parse(s)!;

    [Theory]
    [InlineData("0.9.0-alpha.1", "0.9.0-alpha.2", -1)]
    [InlineData("0.9.0-alpha.2", "0.9.0-beta.1", -1)]
    [InlineData("0.9.0-beta.10", "0.9.0-beta.2", 1)]
    [InlineData("0.9.0-rc.1", "0.9.0", -1)]
    [InlineData("v1.0.0", "1.0", 0)]
    [InlineData("1.0.1", "1.0.0", 1)]
    public void Semantic_version_order(string a, string b, int sign) => Assert.Equal(sign, Math.Sign(V(a).CompareTo(V(b))));

    [Theory]
    [InlineData("latest")]
    [InlineData("1")]
    [InlineData("")]
    public void Invalid_versions_are_null(string s) => Assert.Null(AppVersion.Parse(s));

    static JsonElement Releases(params (string tag, bool pre, bool withX64, bool withSha)[] rs) =>
        JsonDocument.Parse(JsonSerializer.Serialize(rs.Select(r => new
        {
            tag_name = r.tag, prerelease = r.pre, draft = false, html_url = $"https://github.com/o/r/releases/tag/{r.tag}", body = "notes",
            assets = new[]
            {
                r.withX64 ? new { name = $"BatteryForecast-Setup-{r.tag.TrimStart('v')}-x64.exe", size = 10L,
                    browser_download_url = $"https://github.com/o/r/releases/download/{r.tag}/x64.exe" } : null,
                r.withSha ? new { name = $"BatteryForecast-Setup-{r.tag.TrimStart('v')}-x64.exe.sha256", size = 1L,
                    browser_download_url = $"https://github.com/o/r/releases/download/{r.tag}/x64.exe.sha256" } : null,
                new { name = $"BatteryForecast-Setup-{r.tag.TrimStart('v')}-arm64.exe", size = 11L,
                    browser_download_url = $"https://github.com/o/r/releases/download/{r.tag}/arm64.exe" },
            }.Where(a => a is not null),
        }))).RootElement;

    [Fact]
    public void Prereleases_are_offered_only_to_prerelease_users()
    {
        var rs = Releases(("v0.9.0-beta.1", true, true, true), ("v0.8.0", false, true, true));
        Assert.Equal("0.9.0-beta.1", UpdateService.Pick(rs, V("0.9.0-alpha.1"), "x64")!.Version.ToString());
        Assert.Null(UpdateService.Pick(rs, V("0.8.0"), "x64"));
    }

    [Fact]
    public void Picks_the_newest_with_an_installer_for_this_architecture()
    {
        var rs = Releases(("v1.1.0", false, false, false), ("v1.0.1", false, true, true), ("v1.0.0", false, true, true));
        var x64 = UpdateService.Pick(rs, V("1.0.0"), "x64")!;
        Assert.Equal("1.0.1", x64.Version.ToString());
        Assert.NotNull(x64.ChecksumUrl);
        Assert.Equal("1.1.0", UpdateService.Pick(rs, V("1.0.0"), "arm64")!.Version.ToString());
        Assert.Null(UpdateService.Pick(rs, V("1.1.0"), "arm64"));
    }

    [Fact]
    public void Checksum_file_and_hosts()
    {
        var h = new string('a', 64);
        Assert.Equal(h, UpdateService.ParseChecksum($"{h}  BF.exe\n", "BF.exe"));
        Assert.Equal(h, UpdateService.ParseChecksum($"{h} *BF.exe", "BF.exe"));
        Assert.Null(UpdateService.ParseChecksum($"{h}  other.exe", "BF.exe"));
        Assert.Null(UpdateService.ParseChecksum("not a hash", "BF.exe"));
        Assert.True(UpdateService.IsAllowed(new Uri("https://objects.githubusercontent.com/x")));
        Assert.False(UpdateService.IsAllowed(new Uri("http://github.com/x")));
        Assert.False(UpdateService.IsAllowed(new Uri("https://github.com.evil.example/x")));
    }

    sealed class FakeHandler(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(files.TryGetValue(req.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body), RequestMessage = req }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = req });
    }

    static UpdateInfo Info(string sha) => new(V("1.0.1"), "v1.0.1", "https://github.com/o/r", "https://github.com/o/r/x64.exe",
        "BatteryForecast-Setup-1.0.1-x64.exe", 4, sha, "");

    [Fact]
    public async Task Download_is_kept_only_if_the_hash_matches()
    {
        var installer = Encoding.ASCII.GetBytes("fake installer");
        var good = Convert.ToHexStringLower(SHA256.HashData(installer)) + "  BatteryForecast-Setup-1.0.1-x64.exe\n";
        var files = new Dictionary<string, byte[]>
        {
            ["https://github.com/o/r/x64.exe"] = installer,
            ["https://github.com/o/r/good.sha256"] = Encoding.ASCII.GetBytes(good),
            ["https://github.com/o/r/bad.sha256"] = Encoding.ASCII.GetBytes(new string('0', 64) + "  BatteryForecast-Setup-1.0.1-x64.exe"),
        };
        var service = new UpdateService(new HttpClient(new FakeHandler(files)));
        var path = await service.DownloadAsync(Info("https://github.com/o/r/good.sha256"));
        Assert.Equal(installer, await File.ReadAllBytesAsync(path));
        File.Delete(path);

        var e = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(Info("https://github.com/o/r/bad.sha256")));
        Assert.Contains("mismatch", e.Message);
        Assert.False(File.Exists(path));
        await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(Info("https://example.com/x.sha256")));
        await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(Info(null!)));
    }
}
