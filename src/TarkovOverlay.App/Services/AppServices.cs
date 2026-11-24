using System.Windows.Threading;
using TarkovOverlay.App.Ocr;
using TarkovOverlay.Core;
using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Presentation;
using TarkovOverlay.Core.Progress;
using TarkovOverlay.Core.Recognition;
using TarkovOverlay.Core.Settings;
using TarkovOverlay.Core.Updates;
using TarkovOverlay.Ocr.Tesseract;

namespace TarkovOverlay.App.Services;

/// <summary>Composition root shared by the windows.</summary>
public sealed class AppServices : IDisposable
{
    public AppPaths Paths { get; }
    public SettingsStore SettingsStore { get; }
    public AppSettings Settings { get; }
    public Tracker Tracker { get; }
    public TarkovDevFeedSource Feed { get; }
    public CatalogService Catalog { get; }
    /// <summary>1.2.5: shared tarkov.dev availability gate and the probe loop that runs while it is down.</summary>
    public FeedAvailability Availability { get; }
    public OutageMonitor Outage { get; }
    public WindowsOcrEngine WindowsOcr { get; }
    public TesseractOcrEngine Tesseract { get; }
    public HoverRecognizer Recognizer { get; }
    public StackCountReader StackReader { get; }
    public TraderOfferReader OfferReader { get; }
    /// <summary>1.2.0: built hideout levels per mode (hideout-progress.json).</summary>
    public HideoutProgressStore Hideout { get; }
    /// <summary>1.2.0: quest completion (the one in-app quest tracker, Map › Quests…) + hideout levels for the card.</summary>
    public IProgressView Progress { get; }

    /// <summary>1.2.6: GitHub release check (launch, 6 h cache) and its latest result.</summary>
    public UpdateChecker Updates { get; }
    public UpdateCheckResult? UpdateResult { get; private set; }
    public event Action<UpdateCheckResult>? UpdateResultChanged;

    /// <summary>Runs the release check off the UI thread; never throws, never takes focus.</summary>
    public async Task<UpdateCheckResult> CheckForUpdatesAsync(bool force = false)
    {
        bool enabled = Settings.CheckForUpdates; string? skipped = Settings.SkippedUpdateVersion;
        UpdateCheckResult r;
        try { r = await Task.Run(() => Updates.CheckAsync(enabled, skipped, force: force)).ConfigureAwait(false); }
        catch (Exception ex)
        {
            Log.Write("update check: unexpected " + ex.GetType().Name + ": " + ex.Message);
            r = new UpdateCheckResult(UpdateStatus.Failed, UpdateChecker.CurrentVersion, null, UpdateChecker.ReleasesPage, ex.Message, false);
        }
        UpdateResult = r;
        UpdateResultChanged?.Invoke(r);
        return r;
    }

    /// <summary>"Skip this version": hides the banner until a newer release than <paramref name="v"/> comes out.</summary>
    public void SkipUpdate(AppVersion v)
    {
        Settings.SkippedUpdateVersion = v.ToString(); SaveSoon();
        Log.Write($"update check: user skipped {v}");
        if (UpdateResult is { } r && r.Latest is { } l && l.CompareTo(v) == 0)
        {
            UpdateResult = r with { Status = UpdateStatus.Skipped };
            UpdateResultChanged?.Invoke(UpdateResult);
        }
    }

    // ---------------- 1.2.8: Update now (self-update) ----------------
    public SelfUpdater SelfUpdate { get; }
    /// <summary>What the updater reported after the last relaunch (read once at start; null if no update just ran).</summary>
    public UpdateOutcome? LastUpdateOutcome { get; }
    public SelfUpdateStatus SelfUpdateState { get; private set; } = SelfUpdateStatus.Idle;
    public event Action<SelfUpdateStatus>? SelfUpdateChanged;
    /// <summary>Set by the main window: closes the app so the updater can replace the files.</summary>
    public Action? ExitForUpdate { get; set; }
    private CancellationTokenSource? _updateCts;

    private void SetUpdateState(SelfUpdateStatus st) { SelfUpdateState = st; SelfUpdateChanged?.Invoke(st); }

    /// <summary>
    /// Downloads, verifies and unpacks the latest release, starts the updater and closes the app (it relaunches).
    /// Failures and Cancel leave the install untouched and are shown in the banner / Settings.
    /// </summary>
    public async Task UpdateNowAsync(string[]? relaunchArgs = null)
    {
        if (SelfUpdateState.Busy) return;
        var cts = _updateCts = new CancellationTokenSource();
        SetUpdateState(new(SelfUpdatePhase.Working, "Starting the update…", null));
        var progress = new Progress<UpdateProgress>(p =>
        {
            if (cts.IsCancellationRequested) return;
            var text = p.Total > 0 ? $"{p.Stage} {p.Fraction:P0} ({ReleaseInfo.FormatBytes(p.Bytes)} of {ReleaseInfo.FormatBytes(p.Total)})" : p.Stage;
            SetUpdateState(new(SelfUpdatePhase.Working, text, p.Fraction));
        });
        try
        {
            var prepared = await SelfUpdate.PrepareAsync(UpdateChecker.CurrentVersion, Environment.ProcessId, Log.FilePath, progress, cts.Token, relaunchArgs);
            cts.Token.ThrowIfCancellationRequested();
            SetUpdateState(new(SelfUpdatePhase.Restarting, $"Installing {prepared.Version}: the overlay closes and reopens in a few seconds…", 1));
            SaveNow();
            SelfUpdate.Launch(prepared);
            await Task.Delay(400);
            ExitForUpdate?.Invoke();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Log.Write("update: cancelled by the user; nothing was installed");
            SetUpdateState(new(SelfUpdatePhase.Cancelled, "Update cancelled. Nothing was changed.", null));
        }
        catch (UpdateException ex)
        {
            Log.Write($"update: failed{(ex.NeedsAdmin ? " (needs admin rights)" : "")}: {ex.Message}");
            SetUpdateState(new(SelfUpdatePhase.Failed, ex.Message, null, ex.NeedsAdmin));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Write($"update: failed: {ex.GetType().Name}: {ex.Message}");
            SetUpdateState(new(SelfUpdatePhase.Failed, $"The update couldn't be prepared ({ex.Message}). Nothing was changed; use Open releases page.", null, ex is UnauthorizedAccessException));
        }
        finally { if (ReferenceEquals(_updateCts, cts)) _updateCts = null; cts.Dispose(); }
    }

    public void CancelUpdate()
    {
        try { _updateCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>The item of the last lookup / selection / search (for the Wiki and tarkov.dev shortcuts).</summary>
    public CatalogItem? LastItem { get; private set; }
    public event Action<CatalogItem>? LastItemChanged;
    public void SetLastItem(CatalogItem? item)
    {
        if (item == null) return;
        LastItem = item;
        LastItemChanged?.Invoke(item);
    }

    /// <summary>Card builder with the user's field / filter settings and progress.</summary>
    public CardBuilder Cards(CatalogContext ctx) => new(ctx, Tracker, options: CardOptions.From(Settings, Progress) with { Notice = PriceNotice(ctx) });

    /// <summary>1.2.5: "tarkov.dev down · prices from 1 h ago" (or null) for cards and the search footer.</summary>
    public string? PriceNotice(CatalogContext? ctx) =>
        PriceStatus.CardNote(Availability.State, ctx?.Snapshot.FetchedAt, NewestScan(ctx), DateTimeOffset.UtcNow);

    private CatalogSnapshot? _scanFor; private DateTimeOffset? _scan;
    public DateTimeOffset? NewestScan(CatalogContext? ctx)
    {
        var snap = ctx?.Snapshot;
        if (snap == null) return null;
        lock (this) { if (!ReferenceEquals(snap, _scanFor)) { _scanFor = snap; _scan = PriceStatus.NewestScan(snap); } return _scan; }
    }
    private readonly DispatcherTimer _saveDebounce;

    public AppServices()
    {
        Paths = new AppPaths();
        SettingsStore = new SettingsStore(Paths);
        Settings = SettingsStore.Load();
        Tracker = new Tracker(Settings);
        Availability = FeedAvailability.Shared;
        Availability.Log = Log.Write;
        Feed = new TarkovDevFeedSource(availability: Availability);
        if (Feed.BaseAddress.Host != "json.tarkov.dev") Log.Write($"tarkov.dev: feed host overridden by {TarkovDevFeedSource.BaseUrlEnv} = {Feed.BaseAddress} (test only)");
        Outage = new OutageMonitor(Availability, Feed.ProbeAsync);
        Catalog = new CatalogService(Feed, new CatalogStore(Paths), null, Availability, Outage);
        WindowsOcr = new WindowsOcrEngine();
        Tesseract = new TesseractOcrEngine(AppContext.BaseDirectory);
        Recognizer = new HoverRecognizer(WindowsOcr, Tesseract);
        StackReader = new StackCountReader(WindowsOcr, Tesseract);
        OfferReader = new TraderOfferReader(WindowsOcr, Tesseract);
        Hideout = new HideoutProgressStore(HideoutProgressStore.DefaultFile(Paths));
        // Quest states live in the map feature's quest tracker (single source of truth); none → nothing filtered.
        Progress = new ProgressView(
            (mode, questId) => Map.MapFeature.Instance?.Progress.For(mode).Get(questId) == TarkovOverlay.Maps.Quests.QuestState.Complete,
            Hideout);
        Updates = new UpdateChecker(Paths.UpdateCheckFile) { Log = Log.Write };
        if (Updates.Api != UpdateChecker.LatestApi) Log.Write($"update check: URL overridden by {UpdateChecker.UrlEnv} = {Updates.Api} (test only)");
        SelfUpdate = new SelfUpdater(Updates, Paths.UpdateDir, AppContext.BaseDirectory) { Log = Log.Write };
        LastUpdateOutcome = SelfUpdate.TakeOutcome();
        if (LastUpdateOutcome is { } uo)
            Log.Write(uo.Ok ? $"update: now running {UpdateChecker.CurrentVersion} (updated from {uo.FromVersion}; {uo.FilesReplaced} replaced, {uo.FilesAdded} added)"
                            : $"update: the update to {uo.ToVersion} did not install{(uo.RolledBack ? " and was rolled back" : "")}: {uo.Error}");
        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); SaveNow(); };
    }

    public void SaveSoon() { _saveDebounce.Stop(); _saveDebounce.Start(); }

    public void SaveNow()
    {
        try { SettingsStore.Save(Settings); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _saveDebounce.Stop();
        SaveNow();
        Outage.Dispose();
        Catalog.Dispose();
        Tesseract.Dispose();
        Feed.Dispose();
    }
}

public enum SelfUpdatePhase { Idle, Working, Restarting, Failed, Cancelled }

/// <summary>1.2.8: what Update now is doing, for the banner and Settings › Updates.</summary>
public sealed record SelfUpdateStatus(SelfUpdatePhase Phase, string Text, double? Fraction, bool NeedsAdmin = false)
{
    public static readonly SelfUpdateStatus Idle = new(SelfUpdatePhase.Idle, "", null);
    public bool Busy => Phase is SelfUpdatePhase.Working or SelfUpdatePhase.Restarting;
}
