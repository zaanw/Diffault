using System.Globalization;

namespace ErrorHunt;

public static class ReportMarkdown
{
    public static void Write(TextWriter writer, Report report, string title, bool comparison)
    {
        writer.WriteLine("# Diffault report");
        writer.WriteLine();
        writer.WriteLine(Escape(title));
        writer.WriteLine();

        if (comparison)
        {
            WriteGroup(writer, "Resolved", report.Fixed);
            WriteGroup(writer, "New", report.New);
            WriteGroup(writer, "Persistent", report.Existing);
        }
        else
        {
            WriteGroup(writer, "Diagnostics", report.New);
        }
    }

    private static void WriteGroup(TextWriter writer, string heading, Diagnostic[] items)
    {
        writer.WriteLine(FormattableString.Invariant($"## {heading} ({items.Length})"));
        writer.WriteLine();
        if (items.Length == 0)
        {
            writer.WriteLine("No diagnostics.");
            writer.WriteLine();
            return;
        }

        writer.WriteLine("| Severity | Source | Code | Message | Occurrences |");
        writer.WriteLine("| --- | --- | --- | --- | --- |");
        foreach (var item in items)
        {
            var source = item.File ?? "—";
            if (item.Line is > 0)
            {
                source += FormattableString.Invariant($":{item.Line}");
                if (item.Column is > 0) source += FormattableString.Invariant($":{item.Column}");
            }
            var count = item.PreviousCount is { } previous
                ? FormattableString.Invariant($"{previous} → {item.Count}")
                : item.Count.ToString(CultureInfo.InvariantCulture);
            writer.WriteLine($"| {Escape(item.Severity)} | {Escape(source)} | {Escape(item.Code ?? "—")} | {Escape(item.Message)} | {count} |");
        }
        writer.WriteLine();
    }

    private static string Escape(string value)
    {
        // A scanner can report markup as text. Keep it inert in Markdown viewers.
        var output = new System.Text.StringBuilder(value.Length);
        foreach (var current in value)
        {
            if (char.IsControl(current) || current is '\u2028' or '\u2029')
            {
                output.Append(' ');
                continue;
            }
            if ("\\`*_{}[]<>()#+-.!|~&@:$".Contains(current)) output.Append('\\');
            output.Append(current);
        }
        return output.ToString();
    }
}
