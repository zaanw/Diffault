namespace ErrorHunt;

public static class SourcePath
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".cc", ".cpp", ".cxx", ".h", ".hpp", ".cs", ".go", ".java", ".js",
        ".jsx", ".lua", ".php", ".py", ".rs", ".sql", ".swift", ".ts", ".tsx",
    };

    public static string Verify(string root, string candidate)
    {
        if (candidate.IndexOfAny(['\0', '\r', '\n']) >= 0 ||
            (OperatingSystem.IsWindows() && candidate.Replace('/', '\\').StartsWith(@"\\", StringComparison.Ordinal)))
            throw new IOException("Only local source files inside the selected project are allowed.");

        root = Path.GetFullPath(root);
        if (OperatingSystem.IsWindows() && root.StartsWith(@"\\", StringComparison.Ordinal))
            throw new IOException("Choose a local project folder.");

        var path = Path.GetFullPath(candidate, root);
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.Contains(':') || !Extensions.Contains(Path.GetExtension(path)))
            throw new IOException("Choose a supported source file inside the selected project.");

        // Check all ancestors so junctions or symlinks cannot redirect source navigation.
        for (string? entry = path; entry is not null; entry = Path.GetDirectoryName(entry))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Source navigation through a symlink or junction is disabled.");
        }
        if (!File.Exists(path))
            throw new IOException("Source file was not found.");
        return path;
    }
}
