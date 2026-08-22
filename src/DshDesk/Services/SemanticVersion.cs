namespace DshDesk.Services;

internal sealed class SemanticVersion : IComparable<SemanticVersion>
{
    private SemanticVersion(int major, int minor, int patch, IReadOnlyList<string> preRelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
    }

    internal int Major { get; }

    internal int Minor { get; }

    internal int Patch { get; }

    internal IReadOnlyList<string> PreRelease { get; }

    internal static bool TryParse(string? value, out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith('v'))
        {
            normalized = normalized[1..];
        }

        var buildIndex = normalized.IndexOf('+');
        if (buildIndex >= 0)
        {
            normalized = normalized[..buildIndex];
        }

        var dashIndex = normalized.IndexOf('-');
        var core = dashIndex >= 0 ? normalized[..dashIndex] : normalized;
        var identifiers = dashIndex >= 0
            ? normalized[(dashIndex + 1)..].Split('.', StringSplitOptions.RemoveEmptyEntries)
            : [];
        var parts = core.Split('.');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor) ||
            !int.TryParse(parts[2], out var patch) ||
            major < 0 || minor < 0 || patch < 0 ||
            dashIndex >= 0 && identifiers.Length == 0)
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, identifiers);
        return true;
    }

    internal static bool IsNewer(string? candidate, string? current) =>
        TryParse(candidate, out var candidateVersion) &&
        TryParse(current, out var currentVersion) &&
        candidateVersion!.CompareTo(currentVersion) > 0;

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var coreComparison = Major.CompareTo(other.Major);
        if (coreComparison == 0) coreComparison = Minor.CompareTo(other.Minor);
        if (coreComparison == 0) coreComparison = Patch.CompareTo(other.Patch);
        if (coreComparison != 0)
        {
            return coreComparison;
        }

        if (PreRelease.Count == 0 || other.PreRelease.Count == 0)
        {
            return PreRelease.Count == other.PreRelease.Count ? 0 : PreRelease.Count == 0 ? 1 : -1;
        }

        for (var index = 0; index < Math.Max(PreRelease.Count, other.PreRelease.Count); index++)
        {
            if (index >= PreRelease.Count) return -1;
            if (index >= other.PreRelease.Count) return 1;

            var leftNumeric = int.TryParse(PreRelease[index], out var leftNumber);
            var rightNumeric = int.TryParse(other.PreRelease[index], out var rightNumber);
            int comparison;
            if (leftNumeric && rightNumeric)
            {
                comparison = leftNumber.CompareTo(rightNumber);
            }
            else if (leftNumeric != rightNumeric)
            {
                comparison = leftNumeric ? -1 : 1;
            }
            else
            {
                comparison = string.Compare(PreRelease[index], other.PreRelease[index], StringComparison.Ordinal);
            }

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }
}
