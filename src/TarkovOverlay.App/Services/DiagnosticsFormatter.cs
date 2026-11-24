using System.Text;
using TarkovOverlay.Core.Detection;
using TarkovOverlay.Core.Recognition;

namespace TarkovOverlay.App.Services;

public static class DiagnosticsFormatter
{
    public static string Format(LookupReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{r.At:yyyy-MM-dd HH:mm:ss} (local) · total {r.Elapsed.TotalMilliseconds:0} ms · {r.Summary}");
        sb.AppendLine($"Cursor {r.Cursor.X},{r.Cursor.Y} · monitor {Rect(r.Monitor.Bounds)} work {Rect(r.Monitor.WorkArea)} · DPI scale {r.Monitor.DpiScale:0.##}");
        sb.AppendLine($"Capture {Rect(r.FirstCrop)}{(r.WideCrop is { } w ? $" → wide retry {Rect(w)}" : "")}");
        AppendDetection(sb, r.Detection);
        AppendOutcome(sb, r.Outcome);
        if (r.Stack is { } st)
            sb.AppendLine($"Stack count: {(st.Count?.ToString() ?? "not read")} · {st.Note}{(st.Reads.Count > 0 ? " · " + string.Join(" | ", st.Reads) : "")}");
        return sb.ToString();
    }

    public static void AppendDetection(StringBuilder sb, DetectionResult? d)
    {
        if (d == null) return;
        sb.AppendLine($"Detection: {d.Status}{(d.Best is { } b ? $" · panel {Rect(b.Interior)} title {Rect(b.TitlePanel)} text {Rect(b.TextBounds)} lines {b.Lines} (name {b.TitleLines})" : "")}");
        foreach (var c in d.Considered.Take(8))
            sb.AppendLine($"  candidate {Rect(c.Interior)} {(c.Accepted ? "accepted" : "rejected")} · {c.Reason} · score {c.Score:0.##}");
    }

    public static void AppendOutcome(StringBuilder sb, RecognitionOutcome? o)
    {
        if (o == null) return;
        sb.AppendLine($"Recognition {o.Elapsed.TotalMilliseconds:0} ms · {o.Result.Kind} · {string.Join(" | ", o.Result.Items.Take(6).Select(i => i.Name))}");
        foreach (var a in o.Attempts)
            sb.AppendLine($"  {a.Read.Engine} {a.Read.Scale:0.##}× ({a.Read.ImageWidth}×{a.Read.ImageHeight}, {a.Read.Elapsed.TotalMilliseconds:0} ms): \"{a.Read.Text}\"{(a.Read.RawText != a.Read.Text ? $" (raw \"{a.Read.RawText}\")" : "")} → {a.Match.Kind}{(a.Read.Error != null ? " · error: " + a.Read.Error : "")}");
        foreach (var n in o.Notes) sb.AppendLine("  note: " + n);
    }

    public static string Rect(PixelRect r) => $"[{r.X},{r.Y} {r.Width}×{r.Height}]";
}
