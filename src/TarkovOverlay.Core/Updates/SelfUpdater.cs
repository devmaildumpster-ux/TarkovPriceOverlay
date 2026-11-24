using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace TarkovOverlay.Core.Updates;

/// <summary>Progress of Update now: what it's doing, and bytes so far / total while downloading.</summary>
public sealed record UpdateProgress(string Stage, long Bytes = 0, long Total = 0)
{
    public double? Fraction => Total > 0 ? Math.Clamp((double)Bytes / Total, 0, 1) : null;
}

/// <summary>Everything is downloaded, checked and unpacked; the updater can take over once the app exits.</summary>
public sealed record PreparedUpdate(UpdatePlan Plan, string PlanFile, string UpdaterPath, AppVersion Version, AssetChoice Choice);

/// <summary>
/// 1.2.8 Update now. Downloads the release file (update zip for self-contained installs, else the full zip) from
/// this repo's latest GitHub release over HTTPS, checks its size and SHA-256 (GitHub's digest and/or SHA256SUMS.txt),
/// unpacks and validates it into %LOCALAPPDATA%\TarkovPriceOverlay\update\staging, copies the updater there and writes
/// plan.json. The app then starts the updater and exits; the updater swaps the files (with backup + rollback) and
/// relaunches the overlay, which reads last-update.json and shows "Updated to X.Y.Z".
/// </summary>
public sealed class SelfUpdater
{
    private readonly UpdateChecker _checker;
    private readonly HttpMessageHandler? _handler;
    public string UpdateRoot { get; }
    public string InstallDir { get; }
    public Action<string>? Log { get; set; }

    public SelfUpdater(UpdateChecker checker, string updateRoot, string installDir, HttpMessageHandler? handler = null)
    {
        _checker = checker; _handler = handler;
        UpdateRoot = updateRoot; InstallDir = Path.GetFullPath(installDir);
    }

    public string StagingDir => Path.Combine(UpdateRoot, "staging");
    public string DownloadDir => Path.Combine(UpdateRoot, "download");
    public string BackupDir => Path.Combine(UpdateRoot, "backup");
    public string PlanFile => Path.Combine(UpdateRoot, "plan.json");
    public string ResultFile => Path.Combine(UpdateRoot, "last-update.json");
    public string UpdaterCopy => Path.Combine(UpdateRoot, UpdatePackage.UpdaterExe);

    private void Say(string m) => Log?.Invoke(m);

    public async Task<PreparedUpdate> PrepareAsync(AppVersion current, int pid, string logFile, IProgress<UpdateProgress>? progress = null,
        CancellationToken ct = default, string[]? relaunchArgs = null, Func<string, AppVersion?>? versionOf = null)
    {
        Say($"update: Update now · this {current} · install {InstallDir}");
        if (!File.Exists(Path.Combine(InstallDir, UpdatePackage.ExeName)))
            throw new UpdateException($"Can't find {UpdatePackage.ExeName} in {InstallDir}, so this copy can't update itself. Use Open releases page.");
        if (!UpdatePackage.CanWrite(InstallDir))
            throw new UpdateException($"Windows won't let the overlay change its own folder ({InstallDir}); it needs admin rights there (for example under Program Files). " +
                "Move the TarkovPriceOverlay folder somewhere you own, such as another drive, or install the update by hand from the releases page.", needsAdmin: true);

        progress?.Report(new("Asking GitHub for the latest release…"));
        var release = await _checker.FetchLatestAsync(ct).ConfigureAwait(false);
        if (release.Version is not { } v) throw new UpdateException($"The latest release tag '{release.Tag}' isn't a version.");
        if (release.Draft || release.Prerelease || v.IsPrerelease) throw new UpdateException($"The latest release ({release.Tag}) is a draft or prerelease; not installing it.");
        if (v <= current) throw new UpdateException($"You already have the latest version ({current}).");
        var kind = UpdatePackage.DetectKind(InstallDir);
        var choice = UpdatePackage.SelectAsset(release, kind)
            ?? throw new UpdateException($"Release {v} has no {UpdatePackage.UpdateZipName(v)} or {UpdatePackage.FullZipName} to install. Use Open releases page.");
        var asset = choice.Asset;
        if (asset.Size > UpdatePackage.MaxDownloadBytes) throw new UpdateException($"{asset.Name} is unexpectedly large ({ReleaseInfo.FormatBytes(asset.Size)}); not downloading it.");
        Say($"update: {v} found; {kind} install → {asset.Name} ({ReleaseInfo.FormatBytes(asset.Size)}){(choice.IsFullZip ? " (full zip)" : "")}; GitHub digest {(asset.Sha256 != null ? "present" : "missing")}");

        Uri? testOrigin = _checker.IsOverridden ? _checker.Api : null;
        if (testOrigin != null) Say($"update: TEST MODE: downloads also accepted from {testOrigin.GetLeftPart(UriPartial.Authority)} ({UpdateChecker.UrlEnv})");

        foreach (var d in new[] { StagingDir, DownloadDir }) if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
        Directory.CreateDirectory(DownloadDir);

        IReadOnlyDictionary<string, string>? sums = null;
        if (release.Assets.FirstOrDefault(a => a.Name == UpdatePackage.SumsName) is { } sumsAsset)
        {
            progress?.Report(new("Downloading checksums…"));
            var sumsFile = Path.Combine(DownloadDir, UpdatePackage.SumsName);
            await DownloadAsync(sumsAsset.Url, sumsFile, UpdatePackage.MaxSumsBytes, -1, testOrigin, null, ct).ConfigureAwait(false);
            sums = ReleaseInfo.ParseSha256Sums(await File.ReadAllTextAsync(sumsFile, ct).ConfigureAwait(false));
            Say($"update: {UpdatePackage.SumsName}: {sums.Count} entr{(sums.Count == 1 ? "y" : "ies")}");
        }

        var zip = Path.Combine(DownloadDir, asset.Name);
        var sw = Stopwatch.StartNew();
        await DownloadAsync(asset.Url, zip, UpdatePackage.MaxDownloadBytes, asset.Size, testOrigin,
            (b, t) => progress?.Report(new($"Downloading {v}…", b, t)), ct).ConfigureAwait(false);
        Say($"update: downloaded {asset.Name} ({ReleaseInfo.FormatBytes(new FileInfo(zip).Length)}) in {sw.Elapsed.TotalSeconds:0.0} s");

        progress?.Report(new("Checking the download…"));
        try { UpdatePackage.VerifyHash(zip, asset.Name, asset.Sha256, sums, Say); }
        catch { TryDelete(zip); throw; }

        ct.ThrowIfCancellationRequested();
        progress?.Report(new("Unpacking…"));
        var staged = await Task.Run(() => UpdatePackage.ExtractAndValidate(zip, StagingDir, choice, v, versionOf), ct).ConfigureAwait(false);
        Say($"update: unpacked {staged.Count} file(s) to {StagingDir}; version check ok ({v})");
        TryDelete(zip);

        // Prefer the updater that came with the new version (just verified), else the installed one.
        var updaterSrc = File.Exists(Path.Combine(StagingDir, UpdatePackage.UpdaterExe)) ? Path.Combine(StagingDir, UpdatePackage.UpdaterExe)
            : File.Exists(Path.Combine(InstallDir, UpdatePackage.UpdaterExe)) ? Path.Combine(InstallDir, UpdatePackage.UpdaterExe)
            : throw new UpdateException($"{UpdatePackage.UpdaterExe} is missing, so the update can't be applied automatically. Use Open releases page.");
        File.Copy(updaterSrc, UpdaterCopy, overwrite: true);

        var plan = new UpdatePlan
        {
            InstallDir = InstallDir, StagingDir = StagingDir, BackupDir = BackupDir,
            FromVersion = current.ToString(), ToVersion = v.ToString(), WaitForPid = pid,
            LogFile = logFile, ResultFile = ResultFile, RelaunchArgs = relaunchArgs ?? Array.Empty<string>(),
        };
        UpdateApplier.WritePlan(PlanFile, plan);
        TryDelete(ResultFile);
        Say($"update: ready; plan {PlanFile}; updater {UpdaterCopy}");
        return new PreparedUpdate(plan, PlanFile, UpdaterCopy, v, choice);
    }

    /// <summary>Starts the copied updater (it waits for this process to exit). Never asks for admin rights.</summary>
    public Process Launch(PreparedUpdate p)
    {
        var psi = new ProcessStartInfo(p.UpdaterPath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = UpdateRoot };
        psi.ArgumentList.Add("--plan");
        psi.ArgumentList.Add(p.PlanFile);
        var proc = Process.Start(psi) ?? throw new UpdateException("Couldn't start the updater.");
        Say($"update: started updater (pid {proc.Id}); exiting so it can replace the files");
        return proc;
    }

    /// <summary>After a relaunch: the updater's result (deleted once read) and cleanup of the unpacked files.</summary>
    public UpdateOutcome? TakeOutcome()
    {
        var o = UpdateApplier.ReadOutcome(ResultFile);
        if (o == null) return null;
        TryDelete(ResultFile);
        TryDelete(PlanFile);
        foreach (var d in new[] { StagingDir, DownloadDir })
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return o;
    }

    private async Task DownloadAsync(string url, string dest, long maxBytes, long expectedSize, Uri? testOrigin, Action<long, long>? onProgress, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !UpdatePackage.IsAllowedDownload(uri, testOrigin, firstHop: true))
            throw new UpdateException($"Refusing to download from {url} (only this repo's GitHub release files over HTTPS are allowed).");
        var handler = _handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None };
        using var http = new HttpClient(handler, disposeHandler: _handler == null) { Timeout = TimeSpan.FromMinutes(15) };
        var part = dest + ".part";
        try
        {
            for (int hop = 0; ; hop++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.UserAgent.Add(new ProductInfoHeaderValue("TarkovPriceOverlay", UpdateChecker.CurrentVersion.ToString()));
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                int code = (int)resp.StatusCode;
                if (code is 301 or 302 or 303 or 307 or 308)
                {
                    if (hop >= 5) throw new UpdateException("Too many redirects while downloading.");
                    var loc = resp.Headers.Location ?? throw new UpdateException("GitHub sent a redirect without a target.");
                    var next = loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
                    if (!UpdatePackage.IsAllowedDownload(next, testOrigin, firstHop: false))
                        throw new UpdateException($"The download redirected to {next.GetLeftPart(UriPartial.Authority)}, which isn't GitHub; stopped.");
                    uri = next;
                    continue;
                }
                if (!resp.IsSuccessStatusCode) throw new UpdateException($"The download failed: {(code == 403 || code == 429 ? "GitHub is limiting requests" : "server answered")} {code}.");
                long total = resp.Content.Headers.ContentLength ?? expectedSize;
                if (total > maxBytes) throw new UpdateException($"The download is larger than expected ({ReleaseInfo.FormatBytes(total)}).");
                await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    var buf = new byte[1 << 16]; long got = 0; int n; var last = Stopwatch.StartNew();
                    while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                    {
                        got += n;
                        if (got > maxBytes) throw new UpdateException("The download is larger than expected; stopped.");
                        await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        if (last.ElapsedMilliseconds >= 100) { onProgress?.Invoke(got, total); last.Restart(); }
                    }
                    onProgress?.Invoke(got, total);
                    if (expectedSize >= 0 && got != expectedSize)
                        throw new UpdateException($"The download is {got} bytes but GitHub lists {expectedSize}; it may be incomplete. Try again.");
                }
                File.Move(part, dest, overwrite: true);
                return;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new UpdateException("The download timed out."); }
        catch (HttpRequestException ex) { throw new UpdateException($"The download failed ({ex.Message}).", inner: ex); }
        finally { TryDelete(part); }
    }

    private static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
}
