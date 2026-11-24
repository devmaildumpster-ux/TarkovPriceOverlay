using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using TarkovOverlay.Core.Updates;
using Xunit;

namespace TarkovOverlay.Tests;

/// <summary>1.2.8: Update now: asset choice, URL policy, hashes, zip validation, apply + rollback, and the whole prepare step.</summary>
public class SelfUpdateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tpo-self-" + Guid.NewGuid().ToString("N"));
    public SelfUpdateTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private const string Repo = "https://github.com/devmaildumpster-ux/TarkovPriceOverlay";
    private static readonly AppVersion V128 = new(1, 2, 8);
    private static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static ReleaseAsset A(string name, string? sha = null) => new(name, 100, $"{Repo}/releases/download/v1.2.8/{name}", sha);
    private static ReleaseInfo Rel(params ReleaseAsset[] assets) => new("v1.2.8", V128, $"{Repo}/releases/tag/v1.2.8", false, false, assets);

    // ---------------- asset choice ----------------
    [Fact]
    public void SelfContained_PrefersTheUpdateZipForThisVersion()
    {
        var c = UpdatePackage.SelectAsset(Rel(A("TarkovPriceOverlay-win-x64.zip"), A("TarkovPriceOverlay-update-1.2.8.zip")), InstallKind.SelfContained);
        Assert.Equal("TarkovPriceOverlay-update-1.2.8.zip", c!.Asset.Name);
        Assert.False(c.IsFullZip);
    }

    [Fact]
    public void SelfContained_FallsBackToTheFullZip_WhenNoUpdateZipOrItIsForAnotherVersion()
    {
        var c = UpdatePackage.SelectAsset(Rel(A("TarkovPriceOverlay-win-x64.zip"), A("TarkovPriceOverlay-update-1.2.7.zip")), InstallKind.SelfContained);
        Assert.Equal("TarkovPriceOverlay-win-x64.zip", c!.Asset.Name);
        Assert.True(c.IsFullZip);
    }

    [Fact]
    public void FrameworkDependent_AlwaysTakesTheFullZip()
    {
        var c = UpdatePackage.SelectAsset(Rel(A("TarkovPriceOverlay-update-1.2.8.zip"), A("TarkovPriceOverlay-win-x64.zip")), InstallKind.FrameworkDependent);
        Assert.True(c!.IsFullZip);
        Assert.Null(UpdatePackage.SelectAsset(Rel(A("TarkovPriceOverlay-update-1.2.8.zip")), InstallKind.FrameworkDependent));
    }

    [Fact]
    public void NoUsableAsset_OrNoVersion_GivesNull()
    {
        Assert.Null(UpdatePackage.SelectAsset(Rel(A("SHA256SUMS.txt"), A("source.zip")), InstallKind.SelfContained));
        Assert.Null(UpdatePackage.SelectAsset(Rel(A("TarkovPriceOverlay-win-x64.zip")) with { Version = null }, InstallKind.SelfContained));
    }

    [Fact]
    public void DetectKind_LooksForTheBundledRuntime()
    {
        Assert.Equal(InstallKind.FrameworkDependent, UpdatePackage.DetectKind(_dir));
        File.WriteAllText(Path.Combine(_dir, "hostfxr.dll"), "x");
        Assert.Equal(InstallKind.SelfContained, UpdatePackage.DetectKind(_dir));
    }

    // ---------------- release JSON / checksums ----------------
    [Fact]
    public void ReleaseJson_ParsesAssetsAndDigests()
    {
        var r = ReleaseInfo.Parse("""
            {"tag_name":"v1.2.8","html_url":"https://github.com/x","draft":false,"prerelease":false,
             "assets":[{"name":"TarkovPriceOverlay-update-1.2.8.zip","size":748618,"browser_download_url":"https://github.com/a/b","digest":"sha256:E85D3213C54BE913C061E14B8A65EB37D6950EFFBF314F04E226AFC146002C9A"},
                       {"name":"old.zip","size":1,"browser_download_url":"https://github.com/c","digest":null},
                       {"name":"","browser_download_url":"https://github.com/d"}]}
            """);
        Assert.Equal(V128, r.Version);
        Assert.Equal(2, r.Assets.Count);
        Assert.Equal(748618, r.Assets[0].Size);
        Assert.Equal("e85d3213c54be913c061e14b8a65eb37d6950effbf314f04e226afc146002c9a", r.Assets[0].Sha256);
        Assert.Null(r.Assets[1].Sha256);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("md5:abc")] [InlineData("sha256:xyz")] [InlineData("sha256:e85d")]
    [InlineData("sha512:e85d3213c54be913c061e14b8a65eb37d6950effbf314f04e226afc146002c9a")]
    public void Digest_RejectsAnythingButSha256Hex(string? d) => Assert.Null(ReleaseInfo.ParseDigest(d));

    [Fact]
    public void Sha256Sums_ParsesSha256sumOutput()
    {
        var h1 = new string('a', 64); var h2 = new string('B', 64);
        var m = ReleaseInfo.ParseSha256Sums($"{h1}  TarkovPriceOverlay-win-x64.zip\r\n{h2} *TarkovPriceOverlay-update-1.2.8.zip\n# comment\njunk line\n{new string('c', 63)}  short.zip\n");
        Assert.Equal(2, m.Count);
        Assert.Equal(h1, m["TarkovPriceOverlay-win-x64.zip"]);
        Assert.Equal(h2.ToLowerInvariant(), m["TarkovPriceOverlay-update-1.2.8.zip"]);
    }

    [Fact]
    public void VerifyHash_ChecksDigestAndSums_AndRequiresAtLeastOne()
    {
        var f = Path.Combine(_dir, "x.zip"); var bytes = Encoding.UTF8.GetBytes("hello update");
        File.WriteAllBytes(f, bytes);
        var good = Hex(bytes); var bad = new string('0', 64);
        UpdatePackage.VerifyHash(f, "x.zip", good, null);
        UpdatePackage.VerifyHash(f, "x.zip", null, new Dictionary<string, string> { ["x.zip"] = good });
        UpdatePackage.VerifyHash(f, "x.zip", good, new Dictionary<string, string> { ["x.zip"] = good });
        Assert.Throws<UpdateException>(() => UpdatePackage.VerifyHash(f, "x.zip", bad, null));
        Assert.Throws<UpdateException>(() => UpdatePackage.VerifyHash(f, "x.zip", good, new Dictionary<string, string> { ["x.zip"] = bad }));
        Assert.Throws<UpdateException>(() => UpdatePackage.VerifyHash(f, "x.zip", good, new Dictionary<string, string> { ["other.zip"] = good }));
        Assert.Throws<UpdateException>(() => UpdatePackage.VerifyHash(f, "x.zip", null, null));
    }

    // ---------------- where downloads may come from ----------------
    [Theory]
    [InlineData(Repo + "/releases/download/v1.2.8/TarkovPriceOverlay-update-1.2.8.zip", true, true)]
    [InlineData("https://GitHub.com/devmaildumpster-ux/TarkovPriceOverlay/releases/download/v1.2.8/x.zip", true, true)]
    [InlineData("http://github.com/devmaildumpster-ux/TarkovPriceOverlay/releases/download/v1.2.8/x.zip", true, false)]   // not https
    [InlineData("https://github.com/someone-else/TarkovPriceOverlay/releases/download/v1.2.8/x.zip", true, false)]       // other repo
    [InlineData("https://github.com/devmaildumpster-ux/TarkovPriceOverlay/archive/main.zip", true, false)]              // not a release file
    [InlineData("https://github.com:8443/devmaildumpster-ux/TarkovPriceOverlay/releases/download/v1/x.zip", true, false)]
    [InlineData("https://user@github.com/devmaildumpster-ux/TarkovPriceOverlay/releases/download/v1/x.zip", true, false)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/1/2?sig=x", true, false)]          // CDN only after a redirect
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/1/2?sig=x", false, true)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/2?sig=x", false, true)]
    [InlineData("http://objects.githubusercontent.com/x", false, false)]
    [InlineData("https://objects.githubusercontent.com.evil.example/x", false, false)]
    [InlineData("https://evil.example/devmaildumpster-ux/TarkovPriceOverlay/releases/download/v1/x.zip", true, false)]
    [InlineData("https://github.com/someone-else/repo/releases/download/v1/x.zip", false, false)]                      // redirect back to github.com must still be this repo
    public void DownloadUrlPolicy(string url, bool firstHop, bool allowed) =>
        Assert.Equal(allowed, UpdatePackage.IsAllowedDownload(new Uri(url), null, firstHop));

    [Fact]
    public void TestOverride_AllowsOnlyThatOrigin_AndHttpOnlyOnLoopback()
    {
        var local = new Uri("http://127.0.0.1:8765/api/latest");
        Assert.True(UpdatePackage.IsAllowedDownload(new Uri("http://127.0.0.1:8765/dl/x.zip"), local));
        Assert.False(UpdatePackage.IsAllowedDownload(new Uri("http://127.0.0.1:9999/dl/x.zip"), local));
        Assert.False(UpdatePackage.IsAllowedDownload(new Uri("http://127.0.0.1:8765/dl/x.zip"), null));
        var lan = new Uri("http://192.168.1.5:8765/api");
        Assert.False(UpdatePackage.IsAllowedDownload(new Uri("http://192.168.1.5:8765/dl/x.zip"), lan));
    }

    // ---------------- zip entry names ----------------
    [Theory]
    [InlineData("TarkovPriceOverlay.exe", "TarkovPriceOverlay.exe")]
    [InlineData("licenses/Tesseract-LICENSE.txt", "licenses/Tesseract-LICENSE.txt")]
    [InlineData("licenses\\a.txt", "licenses/a.txt")]
    [InlineData("tessdata/", "tessdata")]
    [InlineData("../evil.dll", null)]
    [InlineData("a/../../evil.dll", null)]
    [InlineData("..\\evil.dll", null)]
    [InlineData("/etc/passwd", null)]
    [InlineData("\\Windows\\x.dll", null)]
    [InlineData("C:/Windows/x.dll", null)]
    [InlineData("C:x.dll", null)]
    [InlineData("TarkovPriceOverlay.exe:stream", null)]
    [InlineData("a//b.txt", null)]
    [InlineData("./a.txt", null)]
    [InlineData("CON", null)]
    [InlineData("licenses/aux.txt", null)]
    [InlineData("COM1.dll", null)]
    [InlineData("trailingdot.", null)]
    [InlineData("", null)]
    public void SafeRelative(string name, string? expected) => Assert.Equal(expected, UpdatePackage.SafeRelative(name));

    // ---------------- unpack + validate ----------------
    private string Zip(string name, params (string Path, string Content)[] entries)
    {
        var p = Path.Combine(_dir, name);
        using var z = ZipFile.Open(p, ZipArchiveMode.Create);
        foreach (var (path, content) in entries)
        {
            var e = z.CreateEntry(path);
            using var w = new StreamWriter(e.Open());
            w.Write(content);
        }
        return p;
    }

    private static readonly Func<string, AppVersion?> Is128 = _ => V128;
    private string Staging => Path.Combine(_dir, "staging");
    private static AssetChoice Upd => new(A("TarkovPriceOverlay-update-1.2.8.zip"), false, InstallKind.SelfContained);
    private static AssetChoice Full(InstallKind k) => new(A("TarkovPriceOverlay-win-x64.zip"), true, k);

    [Fact]
    public void UpdateZip_IsUnpackedAsIs()
    {
        var z = Zip("u.zip", ("TarkovPriceOverlay.exe", "exe"), ("TarkovPriceOverlay.dll", "dll"), ("licenses/", ""), ("licenses/a.txt", "lic"), ("CHANGELOG-1.2.8.txt", "notes"));
        var staged = UpdatePackage.ExtractAndValidate(z, Staging, Upd, V128, Is128);
        Assert.Equal(4, staged.Count);
        Assert.Equal("lic", File.ReadAllText(Path.Combine(Staging, "licenses", "a.txt")));
    }

    [Fact]
    public void FullZip_UnpacksOnlyThisInstallsFolder()
    {
        var z = Zip("f.zip",
            ("TarkovPriceOverlay/README.md", "r"),
            ("TarkovPriceOverlay/self-contained/TarkovPriceOverlay.exe", "sc-exe"), ("TarkovPriceOverlay/self-contained/TarkovPriceOverlay.dll", "sc"),
            ("TarkovPriceOverlay/self-contained/coreclr.dll", "rt"),
            ("TarkovPriceOverlay/framework-dependent/TarkovPriceOverlay.exe", "fd-exe"), ("TarkovPriceOverlay/framework-dependent/TarkovPriceOverlay.dll", "fd"));
        var sc = UpdatePackage.ExtractAndValidate(z, Staging, Full(InstallKind.SelfContained), V128, Is128);
        Assert.Equal(3, sc.Count);
        Assert.Equal("sc-exe", File.ReadAllText(Path.Combine(Staging, "TarkovPriceOverlay.exe")));
        var fd = UpdatePackage.ExtractAndValidate(z, Staging, Full(InstallKind.FrameworkDependent), V128, Is128);
        Assert.Equal(2, fd.Count);
        Assert.False(File.Exists(Path.Combine(Staging, "coreclr.dll")));  // staging is emptied first
        Assert.Equal("fd-exe", File.ReadAllText(Path.Combine(Staging, "TarkovPriceOverlay.exe")));
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("..\\evil.dll")]
    [InlineData("sub/../../evil.dll")]
    [InlineData("/abs.dll")]
    [InlineData("C:/Windows/evil.dll")]
    public void ZipSlip_IsRefused_AndNothingLandsOutside(string bad)
    {
        var z = Zip("bad.zip", ("TarkovPriceOverlay.exe", "exe"), ("TarkovPriceOverlay.dll", "dll"), (bad, "pwn"));
        Assert.Throws<UpdateException>(() => UpdatePackage.ExtractAndValidate(z, Staging, Upd, V128, Is128));
        Assert.False(File.Exists(Path.Combine(_dir, "evil.dll")));
    }

    [Fact]
    public void Layout_Errors()
    {
        var noExe = Zip("a.zip", ("TarkovPriceOverlay.dll", "dll"));
        Assert.Contains("TarkovPriceOverlay.exe", Assert.Throws<UpdateException>(() => UpdatePackage.ExtractAndValidate(noExe, Staging, Upd, V128, Is128)).Message);
        var fullAsUpdate = Zip("b.zip", ("TarkovPriceOverlay/self-contained/TarkovPriceOverlay.exe", "x"), ("TarkovPriceOverlay/self-contained/TarkovPriceOverlay.dll", "x"));
        Assert.Throws<UpdateException>(() => UpdatePackage.ExtractAndValidate(fullAsUpdate, Staging, Upd, V128, Is128));
        var updateAsFull = Zip("c.zip", ("TarkovPriceOverlay.exe", "x"), ("TarkovPriceOverlay.dll", "x"));
        Assert.Throws<UpdateException>(() => UpdatePackage.ExtractAndValidate(updateAsFull, Staging, Full(InstallKind.SelfContained), V128, Is128));
        var fdOnly = Zip("d.zip", ("TarkovPriceOverlay/framework-dependent/TarkovPriceOverlay.exe", "x"), ("TarkovPriceOverlay/framework-dependent/TarkovPriceOverlay.dll", "x"));
        Assert.Throws<UpdateException>(() => UpdatePackage.ExtractAndValidate(fdOnly, Staging, Full(InstallKind.SelfContained), V128, Is128));
        var notZip = Path.Combine(_dir, "e.zip"); File.WriteAllText(notZip, "<html>rate limited</html>");
        Assert.Throws<UpdateException>(() => UpdatePackage.ExtractAndValidate(notZip, Staging, Upd, V128, Is128));
    }

    [Fact]
    public void WrongVersionInside_IsRefused()
    {
        var z = Zip("v.zip", ("TarkovPriceOverlay.exe", "exe"), ("TarkovPriceOverlay.dll", "dll"));
        var ex = Assert.Throws<UpdateException>(() => UpdatePackage.ExtractAndValidate(z, Staging, Upd, V128, _ => new AppVersion(1, 2, 7)));
        Assert.Contains("1.2.7", ex.Message);
        Assert.Throws<UpdateException>(() => UpdatePackage.ExtractAndValidate(z, Staging, Upd, V128, _ => null));
    }

    [Fact]
    public void AssemblyVersionOf_ReadsARealDll_AndRejectsJunk()
    {
        Assert.Equal(UpdateChecker.CurrentVersion, UpdatePackage.AssemblyVersionOf(typeof(UpdateChecker).Assembly.Location));
        var junk = Path.Combine(_dir, "junk.dll"); File.WriteAllText(junk, "MZ not really");
        Assert.Null(UpdatePackage.AssemblyVersionOf(junk));
    }

    // ---------------- apply + rollback ----------------
    private (UpdatePlan Plan, string Install) MakeInstall()
    {
        var install = Path.Combine(_dir, "install"); var staging = Path.Combine(_dir, "update", "staging");
        Directory.CreateDirectory(Path.Combine(install, "licenses")); Directory.CreateDirectory(Path.Combine(staging, "licenses"));
        File.WriteAllText(Path.Combine(install, "TarkovPriceOverlay.exe"), "old exe");
        File.WriteAllText(Path.Combine(install, "TarkovPriceOverlay.dll"), "old dll");
        File.WriteAllText(Path.Combine(install, "TarkovOverlay.Core.dll"), "old core");
        File.WriteAllText(Path.Combine(install, "settings.json"), "{\"mine\":true}");
        File.WriteAllText(Path.Combine(install, "untouched.dll"), "keep");
        File.WriteAllText(Path.Combine(staging, "TarkovPriceOverlay.exe"), "new exe");
        File.WriteAllText(Path.Combine(staging, "TarkovPriceOverlay.dll"), "new dll");
        File.WriteAllText(Path.Combine(staging, "TarkovOverlay.Core.dll"), "new core");
        File.WriteAllText(Path.Combine(staging, "TarkovPriceOverlay.Updater.exe"), "updater");
        File.WriteAllText(Path.Combine(staging, "licenses", "new-LICENSE.txt"), "lic");
        File.WriteAllText(Path.Combine(staging, "settings.json"), "{\"mine\":false}");
        File.WriteAllText(Path.Combine(staging, "CHANGELOG-1.2.8.txt"), "notes");
        File.WriteAllText(Path.Combine(staging, "UPDATE-README.txt"), "notes");
        var plan = new UpdatePlan { InstallDir = install, StagingDir = staging, BackupDir = Path.Combine(_dir, "update", "backup"), FromVersion = "1.2.7", ToVersion = "1.2.8" };
        return (plan, install);
    }

    private static string R(string dir, params string[] p) => File.ReadAllText(Path.Combine(new[] { dir }.Concat(p).ToArray()));

    [Fact]
    public void Apply_ReplacesAndAdds_BacksUp_AndNeverTouchesUserFiles()
    {
        var (plan, install) = MakeInstall();
        var log = new List<string>();
        var o = UpdateApplier.Apply(plan, log.Add, retryDelayMs: 1);
        Assert.True(o.Ok, o.Error);
        Assert.Equal(3, o.FilesReplaced);
        Assert.Equal(2, o.FilesAdded);   // updater + license
        Assert.Equal("new exe", R(install, "TarkovPriceOverlay.exe"));
        Assert.Equal("new core", R(install, "TarkovOverlay.Core.dll"));
        Assert.Equal("lic", R(install, "licenses", "new-LICENSE.txt"));
        Assert.Equal("{\"mine\":true}", R(install, "settings.json"));
        Assert.Equal("keep", R(install, "untouched.dll"));
        Assert.False(File.Exists(Path.Combine(install, "CHANGELOG-1.2.8.txt")));
        Assert.False(File.Exists(Path.Combine(install, "UPDATE-README.txt")));
        Assert.Equal("old exe", R(plan.BackupDir, "TarkovPriceOverlay.exe"));
        Assert.Empty(Directory.GetFiles(install, "*.tpo-new", SearchOption.AllDirectories));
        Assert.Contains(log, l => l.Contains("skipped settings.json"));
    }

    [Theory]
    [InlineData("TarkovOverlay.Core.dll")]   // after the first files went in
    [InlineData("TarkovPriceOverlay.exe")]   // the very last file
    [InlineData("licenses/new-LICENSE.txt")] // a new file
    public void Apply_FailureHalfway_RollsEverythingBack(string failOn)
    {
        var (plan, install) = MakeInstall();
        var before = Directory.GetFiles(install, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllText);
        var o = UpdateApplier.Apply(plan, _ => { }, rel => { if (rel == failOn) throw new IOException("disk full (simulated)"); }, retryDelayMs: 1);
        Assert.False(o.Ok);
        Assert.True(o.RolledBack);
        Assert.Contains("disk full", o.Error);
        var after = Directory.GetFiles(install, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllText);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Apply_RefusesAFolderThatIsNotTheOverlay()
    {
        var (plan, install) = MakeInstall();
        File.Delete(Path.Combine(install, "TarkovPriceOverlay.exe"));
        var o = UpdateApplier.Apply(plan, _ => { });
        Assert.False(o.Ok);
        Assert.False(o.RolledBack);
        Assert.False(File.Exists(Path.Combine(install, "TarkovOverlay.Updater.exe")));
        Assert.Equal("old core", R(install, "TarkovOverlay.Core.dll"));
    }

    [Fact]
    public void Apply_ReadOnlyInstallFolder_ReportsNeedsAdmin_AndChangesNothing()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return; // chmod has no effect there
        var (plan, install) = MakeInstall();
        File.SetUnixFileMode(install, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var o = UpdateApplier.Apply(plan, _ => { });
            Assert.False(o.Ok);
            Assert.True(o.NeedsAdmin);
            Assert.Contains("admin", o.Error);
            Assert.Equal("old exe", R(install, "TarkovPriceOverlay.exe"));
        }
        finally { File.SetUnixFileMode(install, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Theory]
    [InlineData("settings.json", true)] [InlineData("cache/pve/catalog.json", true)] [InlineData("logs/overlay.log", true)] [InlineData("x.log", true)]
    [InlineData("update-check.json", true)] [InlineData("CHANGELOG-1.2.8.txt", true)] [InlineData("UPDATE-README.txt", true)]
    [InlineData("TarkovPriceOverlay.exe", false)] [InlineData("licenses/Tesseract-LICENSE.txt", false)] [InlineData("README.md", false)] [InlineData("tessdata/eng.traineddata", false)]
    public void SkippedNames(string rel, bool skipped) => Assert.Equal(skipped, UpdateApplier.IsSkipped(rel));

    [Fact]
    public void PlanAndOutcome_RoundTripAsJson()
    {
        var f = Path.Combine(_dir, "plan.json");
        UpdateApplier.WritePlan(f, new UpdatePlan { InstallDir = "C:\\x", ToVersion = "1.2.8", WaitForPid = 42, RelaunchArgs = new[] { "--a" } });
        var p = UpdateApplier.ReadPlan(f)!;
        Assert.Equal(42, p.WaitForPid); Assert.Equal("--a", p.RelaunchArgs.Single());
        var g = Path.Combine(_dir, "o.json");
        UpdateApplier.WriteOutcome(g, new UpdateOutcome { Ok = true, ToVersion = "1.2.8", FilesReplaced = 3 });
        Assert.Equal(3, UpdateApplier.ReadOutcome(g)!.FilesReplaced);
        File.WriteAllText(g, "{broken");
        Assert.Null(UpdateApplier.ReadOutcome(g));
    }

    // ---------------- the whole prepare step against a fake GitHub ----------------
    private sealed class FakeGitHub : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new(HttpStatusCode.NotFound);
        public List<Uri> Requests = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            Requests.Add(r.RequestUri!);
            return Respond(r);
        }
    }

    private (SelfUpdater Up, FakeGitHub Gh, string Install, byte[] Zip) MakeUpdater(string? digest = "auto", bool sums = false, string cdnHost = "objects.githubusercontent.com")
    {
        var install = Path.Combine(_dir, "inst"); Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "TarkovPriceOverlay.exe"), "old");
        File.WriteAllText(Path.Combine(install, "hostfxr.dll"), "rt");
        File.WriteAllText(Path.Combine(install, "TarkovPriceOverlay.Updater.exe"), "old updater");
        var zipPath = Zip("upd.zip", ("TarkovPriceOverlay.exe", "new"), ("TarkovPriceOverlay.dll", "dll"), ("TarkovPriceOverlay.Updater.exe", "new updater"));
        var zip = File.ReadAllBytes(zipPath);
        var hex = Hex(zip);
        var d = digest == "auto" ? $"\"sha256:{hex}\"" : digest == null ? "null" : $"\"{digest}\"";
        var assets = $"{{\"name\":\"TarkovPriceOverlay-update-1.2.8.zip\",\"size\":{zip.Length},\"browser_download_url\":\"{Repo}/releases/download/v1.2.8/TarkovPriceOverlay-update-1.2.8.zip\",\"digest\":{d}}}";
        if (sums) assets += $",{{\"name\":\"SHA256SUMS.txt\",\"size\":100,\"browser_download_url\":\"{Repo}/releases/download/v1.2.8/SHA256SUMS.txt\"}}";
        var json = $"{{\"tag_name\":\"v1.2.8\",\"html_url\":\"{Repo}/releases/tag/v1.2.8\",\"draft\":false,\"prerelease\":false,\"assets\":[{assets}]}}";
        var gh = new FakeGitHub();
        gh.Respond = r =>
        {
            var u = r.RequestUri!;
            if (u.Host == "api.github.com") return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            if (u.Host == "github.com" && u.AbsolutePath.EndsWith(".zip"))
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Found);
                resp.Headers.Location = new Uri($"https://{cdnHost}/github-production-release-asset/123?sig=abc");
                return resp;
            }
            if (u.Host == "github.com" && u.AbsolutePath.EndsWith("SHA256SUMS.txt"))
                return new(HttpStatusCode.OK) { Content = new StringContent($"{hex}  TarkovPriceOverlay-update-1.2.8.zip\n") };
            if (u.Host == cdnHost) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
            return new(HttpStatusCode.NotFound);
        };
        var checker = new UpdateChecker(null, gh, api: UpdateChecker.LatestApi);
        var up = new SelfUpdater(checker, Path.Combine(_dir, "update"), install, gh);
        return (up, gh, install, zip);
    }

    [Fact]
    public async Task Prepare_DownloadsVerifiesUnpacks_AndWritesThePlan()
    {
        var (up, gh, install, _) = MakeUpdater(sums: true);
        var stages = new List<UpdateProgress>();
        var p = await up.PrepareAsync(new AppVersion(1, 2, 7), 4242, Path.Combine(_dir, "overlay.log"), new SyncProgress(stages.Add), versionOf: Is128);
        Assert.Equal(V128, p.Version);
        Assert.False(p.Choice.IsFullZip);
        Assert.Equal("new", R(up.StagingDir, "TarkovPriceOverlay.exe"));
        Assert.Equal("new updater", File.ReadAllText(up.UpdaterCopy));        // the new version's updater is used
        var plan = UpdateApplier.ReadPlan(up.PlanFile)!;
        Assert.Equal(4242, plan.WaitForPid);
        Assert.Equal(Path.GetFullPath(install), plan.InstallDir);
        Assert.Equal("1.2.7", plan.FromVersion); Assert.Equal("1.2.8", plan.ToVersion);
        Assert.Contains(gh.Requests, u => u.Host == "objects.githubusercontent.com");
        Assert.Contains(gh.Requests, u => u.AbsolutePath.EndsWith("SHA256SUMS.txt"));
        Assert.Contains(stages, s => s.Total > 0 && s.Bytes == s.Total);
        Assert.Equal("old", R(install, "TarkovPriceOverlay.exe"));            // nothing installed yet
        Assert.False(File.Exists(Path.Combine(up.DownloadDir, "TarkovPriceOverlay-update-1.2.8.zip")));
    }

    [Fact]
    public async Task Prepare_FollowsTheNewReleaseAssetsHostToo()
    {
        var (up, _, _, _) = MakeUpdater(cdnHost: "release-assets.githubusercontent.com");
        await up.PrepareAsync(new AppVersion(1, 2, 7), 1, "", versionOf: Is128);
        Assert.True(File.Exists(up.PlanFile));
    }

    [Fact]
    public async Task Prepare_RejectsATamperedDownload_AndLeavesNoPlan()
    {
        var (up, _, _, _) = MakeUpdater(digest: "sha256:" + new string('0', 64));
        var ex = await Assert.ThrowsAsync<UpdateException>(() => up.PrepareAsync(new AppVersion(1, 2, 7), 1, "", versionOf: Is128));
        Assert.Contains("checksum", ex.Message);
        Assert.False(File.Exists(up.PlanFile));
        Assert.False(Directory.Exists(up.StagingDir));
        Assert.Empty(Directory.GetFiles(up.DownloadDir));
    }

    [Fact]
    public async Task Prepare_WithoutAnyChecksum_IsRefused()
    {
        var (up, _, _, _) = MakeUpdater(digest: null);
        await Assert.ThrowsAsync<UpdateException>(() => up.PrepareAsync(new AppVersion(1, 2, 7), 1, "", versionOf: Is128));
    }

    [Fact]
    public async Task Prepare_RefusesARedirectAwayFromGitHub()
    {
        var (up, _, _, _) = MakeUpdater(cdnHost: "evil.example");
        var ex = await Assert.ThrowsAsync<UpdateException>(() => up.PrepareAsync(new AppVersion(1, 2, 7), 1, "", versionOf: Is128));
        Assert.Contains("isn't GitHub", ex.Message);
    }

    [Fact]
    public async Task Prepare_WhenAlreadyCurrent_DoesNothing()
    {
        var (up, gh, _, _) = MakeUpdater();
        var ex = await Assert.ThrowsAsync<UpdateException>(() => up.PrepareAsync(V128, 1, "", versionOf: Is128));
        Assert.Contains("latest version", ex.Message);
        Assert.DoesNotContain(gh.Requests, u => u.Host != "api.github.com");
    }

    [Fact]
    public async Task Prepare_Cancel_StopsAndCleansUp()
    {
        var (up, _, _, _) = MakeUpdater();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => up.PrepareAsync(new AppVersion(1, 2, 7), 1, "", ct: cts.Token, versionOf: Is128));
        Assert.False(File.Exists(up.PlanFile));
    }

    [Fact]
    public async Task Prepare_NotAnOverlayFolder_IsRefusedBeforeAnyRequest()
    {
        var (up, gh, install, _) = MakeUpdater();
        File.Delete(Path.Combine(install, "TarkovPriceOverlay.exe"));
        await Assert.ThrowsAsync<UpdateException>(() => up.PrepareAsync(new AppVersion(1, 2, 7), 1, "", versionOf: Is128));
        Assert.Empty(gh.Requests);
    }

    [Fact]
    public async Task PrepareThenApply_EndToEnd_InTempFolders()
    {
        var (up, _, install, _) = MakeUpdater();
        var p = await up.PrepareAsync(new AppVersion(1, 2, 7), 1, "", versionOf: Is128);
        var o = UpdateApplier.Apply(p.Plan, _ => { }, retryDelayMs: 1);
        Assert.True(o.Ok, o.Error);
        Assert.Equal("new", R(install, "TarkovPriceOverlay.exe"));
        Assert.Equal("new updater", R(install, "TarkovPriceOverlay.Updater.exe"));
        UpdateApplier.WriteOutcome(up.ResultFile, o);
        var taken = up.TakeOutcome();
        Assert.True(taken!.Ok);
        Assert.Null(up.TakeOutcome());                      // read once
        Assert.False(Directory.Exists(up.StagingDir));      // cleaned after the relaunch
    }

    private sealed class SyncProgress : IProgress<UpdateProgress>
    {
        private readonly Action<UpdateProgress> _a; public SyncProgress(Action<UpdateProgress> a) => _a = a;
        public void Report(UpdateProgress v) => _a(v);
    }
}
