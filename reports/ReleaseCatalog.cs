using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ErrorHunt;

public sealed record ReleaseCandidate(string Tag)
{
    public Uri Page => new($"https://github.com/zaanw/Diffault/releases/tag/{Tag}");
}

public static partial class ReleaseCatalog
{
    [GeneratedRegex(@"\Av(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})(?:-beta\.([1-9][0-9]{0,8}))?\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    private static (int Major, int Minor, int Patch, int Beta)? Parse(string? tag)
    {
        if (tag is null) return null;
        var match = VersionPattern().Match(tag);
        if (!match.Success) return null;
        int Number(int group) => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);
        return (Number(1), Number(2), Number(3), match.Groups[4].Success ? Number(4) : int.MaxValue);
    }

    public static ReleaseCandidate? FindUpdate(JsonElement releases, string installedVersion)
    {
        var best = Parse("v" + installedVersion) ?? throw new ArgumentException("Invalid installed version.", nameof(installedVersion));
        if (releases.ValueKind != JsonValueKind.Array) throw new JsonException("Expected a release list.");
        ReleaseCandidate? result = null;
        foreach (var release in releases.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object ||
                !release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
                !release.TryGetProperty("tag_name", out var field) || field.ValueKind != JsonValueKind.String ||
                !release.TryGetProperty("prerelease", out var prerelease) ||
                prerelease.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                continue;

            var tag = field.GetString();
            if (Parse(tag) is not { } version || version.CompareTo(best) <= 0 ||
                prerelease.GetBoolean() != (version.Beta != int.MaxValue) ||
                (prerelease.GetBoolean() && installedVersion.IndexOf("-beta.", StringComparison.Ordinal) < 0))
                continue;

            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array ||
                !assets.EnumerateArray().Any(asset => asset.ValueKind == JsonValueKind.Object &&
                    asset.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                    name.GetString() == $"Diffault-{tag}-win-x64.zip"))
                continue;

            best = version;
            result = new ReleaseCandidate(tag!);
        }
        return result;
    }
}
