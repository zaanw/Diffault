using System.Globalization;

namespace ErrorHunt;

public static class EditorArguments
{
    public static string[] ForSource(string editor, string source, int? line, int? column)
    {
        if (line is not > 0) return [source];

        var row = line.Value.ToString(CultureInfo.InvariantCulture);
        var col = column is > 0 ? column.Value.ToString(CultureInfo.InvariantCulture) : null;
        return Path.GetFileNameWithoutExtension(editor).ToLowerInvariant() switch
        {
            "code" or "code - insiders" or "vscodium" => ["--goto", WithPosition(source, row, col)],
            "sublime_text" => [WithPosition(source, row, col)],
            "notepad++" => col is null ? [$"-n{row}", source] : [$"-n{row}", $"-c{col}", source],
            "idea64" or "rider64" or "pycharm64" or "webstorm64" or "clion64" =>
                col is null ? ["--line", row, source] : ["--line", row, "--column", col, source],
            _ => [source]
        };
    }

    private static string WithPosition(string source, string row, string? col) =>
        col is null ? $"{source}:{row}" : $"{source}:{row}:{col}";
}
