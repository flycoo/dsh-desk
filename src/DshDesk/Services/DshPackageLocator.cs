using System.Text.Json;
using DshDesk.Models;

namespace DshDesk.Services;

public static class DshPackageLocator
{
    public static DshInstallation? FindDshInstallation(string npmCache)
    {
        var npxRoot = Path.Combine(npmCache, "_npx");
        if (!Directory.Exists(npxRoot))
        {
            return null;
        }

        var installations = new List<DshInstallation>();
        foreach (var cacheDirectory in Directory.EnumerateDirectories(npxRoot))
        {
            var packageDirectory = Path.Combine(
                cacheDirectory,
                "node_modules",
                "@deepseek-ai",
                "dsh");
            var packageJsonPath = Path.Combine(packageDirectory, "package.json");
            if (!File.Exists(packageJsonPath))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
                var root = document.RootElement;
                var version = root.GetProperty("version").GetString();
                var entry = ReadBinEntry(root);
                if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(entry))
                {
                    continue;
                }

                var entryPoint = Path.GetFullPath(Path.Combine(packageDirectory, entry));
                if (File.Exists(entryPoint))
                {
                    installations.Add(new DshInstallation(version, packageDirectory, entryPoint));
                }
            }
            catch (JsonException)
            {
                // Ignore incomplete or damaged cache entries and continue scanning.
            }
        }

        return installations
            .OrderByDescending(item => item.Version, SemanticVersionComparer.Instance)
            .FirstOrDefault();
    }

    public static string? FindNodeExecutable()
    {
        var candidates = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => Path.Combine(part.Trim().Trim('"'), "node.exe")));

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            candidates.Add(Path.Combine(programFiles, "nodejs", "node.exe"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    public static string? FindNpxExecutable()
    {
        var node = FindNodeExecutable();
        if (node is not null)
        {
            var besideNode = Path.Combine(Path.GetDirectoryName(node)!, "npx.cmd");
            if (File.Exists(besideNode))
            {
                return besideNode;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => Path.Combine(part.Trim().Trim('"'), "npx.cmd"))
            .FirstOrDefault(File.Exists);
    }

    private static string? ReadBinEntry(JsonElement root)
    {
        if (!root.TryGetProperty("bin", out var bin))
        {
            return null;
        }

        if (bin.ValueKind == JsonValueKind.String)
        {
            return bin.GetString();
        }

        return bin.ValueKind == JsonValueKind.Object &&
               bin.TryGetProperty("dsh", out var dsh)
            ? dsh.GetString()
            : null;
    }

    private sealed class SemanticVersionComparer : IComparer<string>
    {
        public static SemanticVersionComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;

            var leftParts = Parse(left);
            var rightParts = Parse(right);
            var coreComparison = leftParts.Core.CompareTo(rightParts.Core);
            if (coreComparison != 0) return coreComparison;
            if (leftParts.PreRelease.Count == 0 && rightParts.PreRelease.Count > 0) return 1;
            if (rightParts.PreRelease.Count == 0 && leftParts.PreRelease.Count > 0) return -1;

            for (var index = 0; index < Math.Max(leftParts.PreRelease.Count, rightParts.PreRelease.Count); index++)
            {
                if (index >= leftParts.PreRelease.Count) return -1;
                if (index >= rightParts.PreRelease.Count) return 1;
                var comparison = CompareIdentifier(leftParts.PreRelease[index], rightParts.PreRelease[index]);
                if (comparison != 0) return comparison;
            }

            return 0;
        }

        private static (Version Core, List<string> PreRelease) Parse(string value)
        {
            var withoutBuild = value.Split('+', 2)[0];
            var parts = withoutBuild.Split('-', 2);
            var core = Version.TryParse(parts[0], out var parsed) ? parsed : new Version(0, 0, 0);
            var preRelease = parts.Length == 2
                ? parts[1].Split('.', StringSplitOptions.RemoveEmptyEntries).ToList()
                : [];
            return (core, preRelease);
        }

        private static int CompareIdentifier(string left, string right)
        {
            var leftNumeric = int.TryParse(left, out var leftNumber);
            var rightNumeric = int.TryParse(right, out var rightNumber);
            if (leftNumeric && rightNumeric) return leftNumber.CompareTo(rightNumber);
            if (leftNumeric) return -1;
            if (rightNumeric) return 1;
            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
