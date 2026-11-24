using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TarkovOverlay.Maps.Logs;

public enum LogEventKind
{
    SessionMode,      // Value = "pve" / "regular" / "pvp-season"
    MapLoading,       // Value = scene bundle ("maps/customs_preset.bundle"), Extra = rcid ("bigmap")
    MapLoaded,        // Value = location id ("bigmap"), Extra = raid short id; Online flag
    RaidStarting,     // PMC countdown begins
    RaidStarted,
    MatchingAborted,
    RaidExited,       // Value = location id from UserMatchOver
    ProfileSelected,  // back in the menu after a raid (also at game start)
    TaskStatus,       // Value = task id, Extra = "started" / "failed" / "finished"
    ScreenshotKey,    // Value = "ok" / "unbound" / "sysreq" / "axis", Extra = key names (from "Control settings:")
}

public sealed record LogEvent(LogEventKind Kind, DateTimeOffset? Time, string? Value = null, string? Extra = null, bool Online = false);

/// <summary>
/// Incremental parser for Escape from Tarkov's application.log / notifications.log (same lines TarkovMonitor and
/// the tarkov.dev tools use). Feed it text as it is appended; complete entries (including multi-line JSON payloads
/// after "Got notification | …") turn into events.
///
/// Entry format (0.16+): "2025-07-29 03:54:43.464 +02:00|0.16.8.1.38114|Info|application|message"
/// Older builds: "2021-01-10 13:26:41.734 +09:00|0.12.9.10532|Debug|application|message" or without the date.
/// </summary>
public sealed class EftLogParser
{
    private static readonly Regex Header = new(@"^(?<date>\d{4}-\d{2}-\d{2}) (?<time>\d{2}:\d{2}:\d{2}\.\d{3})(?<tz> [+-]\d{2}:\d{2})?\|(?<rest>.*)$", RegexOptions.Compiled);
    private static readonly Regex ShortHeader = new(@"^(?<time>\d{2}:\d{2}:\d{2}\.\d{3})\|(?<rest>.*)$", RegexOptions.Compiled);
    private static readonly Regex ScenePreset = new(@"scene preset path:(?<path>maps/[A-Za-z0-9_]+\.bundle)(?:\s+rcid:(?<rcid>[A-Za-z0-9_]+)\.scenespreset\.asset)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Location = new(@"Location: (?<map>[^,']+)", RegexOptions.Compiled);
    private static readonly Regex ShortId = new(@"shortId: (?<id>[A-Z0-9]{4,8})", RegexOptions.Compiled);
    private static readonly Regex SessionModeRx = new(@"Session mode: (?<mode>[^\s|]+)", RegexOptions.Compiled);

    private readonly StringBuilder _partial = new();
    private Pending? _pending;
    private DateOnly? _assumeDate;

    private sealed class Pending
    {
        public DateTimeOffset? Time;
        public string Message = "";
        public StringBuilder? Json;
        public int Depth;
        public bool InString, Escape;
    }

    /// <summary>For the old date-less format, the date of the log folder.</summary>
    public void AssumeDate(DateOnly d) => _assumeDate = d;

    public List<LogEvent> Feed(string text)
    {
        var events = new List<LogEvent>();
        _partial.Append(text);
        int start = 0;
        var s = _partial.ToString();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\n') continue;
            var line = s.Substring(start, i - start).TrimEnd('\r');
            start = i + 1;
            Line(line, events);
        }
        _partial.Clear();
        if (start < s.Length) _partial.Append(s, start, s.Length - start);
        return events;
    }

    /// <summary>Flushes a pending entry (end of file or before switching files).</summary>
    public List<LogEvent> Flush()
    {
        var events = new List<LogEvent>();
        if (_partial.Length > 0) { Line(_partial.ToString().TrimEnd('\r'), events); _partial.Clear(); }
        Emit(events);
        return events;
    }

    private void Line(string line, List<LogEvent> events)
    {
        var m = Header.Match(line);
        Match? sm = null;
        if (!m.Success) sm = ShortHeader.Match(line);
        if (m.Success || sm!.Success)
        {
            Emit(events);
            var p = new Pending();
            if (m.Success)
            {
                var stamp = m.Groups["date"].Value + " " + m.Groups["time"].Value + (m.Groups["tz"].Success ? m.Groups["tz"].Value : "");
                var fmt = m.Groups["tz"].Success ? "yyyy-MM-dd HH:mm:ss.fff zzz" : "yyyy-MM-dd HH:mm:ss.fff";
                if (DateTimeOffset.TryParseExact(stamp, fmt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t)) p.Time = t;
                p.Message = m.Groups["rest"].Value;
            }
            else
            {
                if (_assumeDate is { } d && TimeOnly.TryParseExact(sm!.Groups["time"].Value, "HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to))
                    p.Time = new DateTimeOffset(d.ToDateTime(to), TimeZoneInfo.Local.GetUtcOffset(d.ToDateTime(to)));
                p.Message = sm!.Groups["rest"].Value;
            }
            _pending = p;
            if (!p.Message.Contains("Got notification", StringComparison.Ordinal) && !p.Message.Contains("Control settings:", StringComparison.Ordinal)) Emit(events);
            return;
        }
        var cur = _pending;
        if (cur == null) return;
        if (cur.Json == null)
        {
            if (!line.TrimStart().StartsWith('{')) return;
            cur.Json = new StringBuilder();
        }
        cur.Json.Append(line).Append('\n');
        foreach (var c in line)
        {
            if (cur.InString)
            {
                if (cur.Escape) cur.Escape = false;
                else if (c == '\\') cur.Escape = true;
                else if (c == '"') cur.InString = false;
                continue;
            }
            if (c == '"') cur.InString = true;
            else if (c == '{') cur.Depth++;
            else if (c == '}') cur.Depth--;
        }
        if (cur.Depth <= 0) Emit(events);
    }

    private void Emit(List<LogEvent> events)
    {
        var p = _pending;
        _pending = null;
        if (p == null) return;
        var e = Interpret(p.Time, p.Message, p.Json?.ToString());
        if (e != null) events.Add(e);
    }

    /// <summary>Turns one log entry (message after the timestamp, optional JSON payload) into an event.</summary>
    public static LogEvent? Interpret(DateTimeOffset? time, string message, string? json)
    {
        if (message.Contains("Session mode: ", StringComparison.Ordinal))
        {
            var mm = SessionModeRx.Match(message);
            var mode = mm.Success ? ModeId(mm.Groups["mode"].Value) : null;
            return mode != null ? new LogEvent(LogEventKind.SessionMode, time, mode) : null;
        }
        if (message.Contains("|scene preset path:", StringComparison.Ordinal) || message.StartsWith("scene preset path:", StringComparison.Ordinal))
        {
            var sp = ScenePreset.Match(message);
            if (!sp.Success) return null;
            return new LogEvent(LogEventKind.MapLoading, time, sp.Groups["path"].Value.ToLowerInvariant(), sp.Groups["rcid"].Success ? sp.Groups["rcid"].Value : null);
        }
        if (message.Contains("TRACE-NetworkGameCreate profileStatus", StringComparison.Ordinal))
        {
            var loc = Location.Match(message);
            if (!loc.Success) return null;
            var sid = ShortId.Match(message);
            return new LogEvent(LogEventKind.MapLoaded, time, loc.Groups["map"].Value.Trim(), sid.Success ? sid.Groups["id"].Value : null,
                Online: message.Contains("RaidMode: Online", StringComparison.Ordinal) || !message.Contains("RaidMode:", StringComparison.Ordinal));
        }
        if (message.Contains("application|GameStarting", StringComparison.Ordinal))
            return new LogEvent(LogEventKind.RaidStarting, time);
        if (message.Contains("application|GameStarted", StringComparison.Ordinal))
            return new LogEvent(LogEventKind.RaidStarted, time);
        if (message.Contains("Network game matching aborted", StringComparison.Ordinal) || message.Contains("Network game matching cancelled", StringComparison.Ordinal))
            return new LogEvent(LogEventKind.MatchingAborted, time);
        if (message.Contains("SelectProfile ProfileId:", StringComparison.Ordinal) || message.Contains("SelectedProfile ProfileId:", StringComparison.Ordinal)
            || message.Contains("PrepareSelectedProfileLocally ProfileId:", StringComparison.Ordinal))
            return new LogEvent(LogEventKind.ProfileSelected, time);
        if (message.Contains("Got notification | UserMatchOver", StringComparison.Ordinal))
        {
            string? loc = null;
            if (json != null) try { using var d = JsonDocument.Parse(json); loc = d.RootElement.TryGetProperty("location", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null; } catch (JsonException) { }
            return new LogEvent(LogEventKind.RaidExited, time, loc);
        }
        if (message.Contains("Got notification | ChatMessageReceived", StringComparison.Ordinal) && json != null)
            return TaskStatusEvent(time, json);
        if (message.Contains("Control settings:", StringComparison.Ordinal))
        {
            var payload = json;
            int brace = message.IndexOf('{');
            if (payload == null && brace >= 0) payload = message[brace..]; // JSON on the same line
            var (status, keys) = ScreenshotKeyCheck.FromControlSettings(payload);
            return status == null ? null : new LogEvent(LogEventKind.ScreenshotKey, time, status, keys);
        }
        return null;
    }

    /// <summary>System messages of type 10/11/12 (quest started/failed/finished); templateId starts with the quest id.</summary>
    internal static LogEvent? TaskStatusEvent(DateTimeOffset? time, string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            if (!d.RootElement.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return null;
            if (!msg.TryGetProperty("type", out var ty) || ty.ValueKind != JsonValueKind.Number || !ty.TryGetInt32(out var type)) return null;
            var status = type switch { 10 => "started", 11 => "failed", 12 => "finished", _ => null };
            if (status == null) return null;
            if (!msg.TryGetProperty("templateId", out var tid) || tid.ValueKind != JsonValueKind.String) return null;
            var id = (tid.GetString() ?? "").Split(' ')[0];
            if (id.Length != 24 || !id.All(Uri.IsHexDigit)) return null;
            return new LogEvent(LogEventKind.TaskStatus, time, id, status);
        }
        catch (JsonException) { return null; }
    }

    public static string? ModeId(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "pve" => "pve",
        "regular" or "pvp" => "regular",
        "pvpseason" or "seasonal" or "szn" => "pvp-season",
        _ => null,
    };
}
