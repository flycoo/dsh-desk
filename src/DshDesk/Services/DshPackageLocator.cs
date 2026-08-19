using System.Diagnostics;
using System.Text.Json;
using DshDesk.Models;

namespace DshDesk.Services;

public static class DshPackageLocator
{
    private const string PackageName = "@deepseek-ai/dsh";

    public static DshInstallation? FindSystemInstallation(
        IEnumerable<string>? pathEntries = null,
        string? applicationData = null,
        Func<string?>? npmGlobalRootProvider = null)
    {
        var paths = (pathEntries ?? GetPathEntries())
            .Select(NormalizeDirectory)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (!HasDshShim(path))
            {
                continue;
            }

            var packageDirectory = Path.Combine(path, "node_modules", "@deepseek-ai", "dsh");
            var installation = TryCandidate(packageDirectory, visited);
            if (installation is not null)
            {
                return installation;
            }
        }

        var roamingAppData = applicationData ??
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(roamingAppData))
        {
            var standardPackageDirectory = Path.Combine(
                roamingAppData,
                "npm",
                "node_modules",
                "@deepseek-ai",
                "dsh");
            var installation = TryCandidate(standardPackageDirectory, visited);
            if (installation is not null)
            {
                return installation;
            }
        }

        var globalRoot = (npmGlobalRootProvider ?? (() => TryGetNpmGlobalRoot(paths)))();
        if (!string.IsNullOrWhiteSpace(globalRoot))
        {
            return TryCandidate(
                Path.Combine(globalRoot.Trim(), "@deepseek-ai", "dsh"),
                visited);
        }

        return null;
    }

    public static bool TryValidatePackageDirectory(
        string? packageDirectory,
        DshInstallationSource source,
        out DshInstallation? installation,
        out string error)
    {
        installation = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(packageDirectory))
        {
            error = "尚未选择 DSH 安装目录。";
            return false;
        }

        string normalizedDirectory;
        try
        {
            normalizedDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(packageDirectory.Trim().Trim('"')));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"DSH 安装路径无效：{exception.Message}";
            return false;
        }

        if (!Directory.Exists(normalizedDirectory))
        {
            error = $"DSH 安装目录不存在：{normalizedDirectory}";
            return false;
        }

        var packageJsonPath = Path.Combine(normalizedDirectory, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            error = "所选目录不包含 package.json。请选择 @deepseek-ai/dsh 包目录。";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("name", out var name) ||
                !string.Equals(name.GetString(), PackageName, StringComparison.Ordinal))
            {
                error = $"所选目录不是官方 {PackageName} 包。";
                return false;
            }

            if (!root.TryGetProperty("version", out var versionProperty) ||
                string.IsNullOrWhiteSpace(versionProperty.GetString()))
            {
                error = "DSH package.json 缺少有效版本号。";
                return false;
            }

            var entry = ReadBinEntry(root);
            if (string.IsNullOrWhiteSpace(entry))
            {
                error = "DSH package.json 缺少 bin.dsh 入口。";
                return false;
            }

            var entryPoint = Path.GetFullPath(Path.Combine(normalizedDirectory, entry));
            var directoryPrefix = normalizedDirectory + Path.DirectorySeparatorChar;
            if (!entryPoint.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                error = "DSH 入口脚本不能位于所选包目录之外。";
                return false;
            }

            if (!File.Exists(entryPoint))
            {
                error = $"DSH 入口脚本不存在：{entryPoint}";
                return false;
            }

            installation = new DshInstallation(
                source,
                versionProperty.GetString()!,
                normalizedDirectory,
                entryPoint);
            return true;
        }
        catch (JsonException exception)
        {
            error = $"DSH package.json 无法解析：{exception.Message}";
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"无法读取 DSH 安装目录：{exception.Message}";
            return false;
        }
    }

    public static string? FindNodeExecutable(IEnumerable<string>? pathEntries = null)
    {
        var candidates = new List<string>();
        candidates.AddRange((pathEntries ?? GetPathEntries())
            .Select(NormalizeDirectory)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.Combine(path, "node.exe")));

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            candidates.Add(Path.Combine(programFiles, "nodejs", "node.exe"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static DshInstallation? TryCandidate(string packageDirectory, ISet<string> visited)
    {
        string normalized;
        try
        {
            normalized = Path.GetFullPath(packageDirectory);
        }
        catch
        {
            return null;
        }

        if (!visited.Add(normalized))
        {
            return null;
        }

        return TryValidatePackageDirectory(
            normalized,
            DshInstallationSource.System,
            out var installation,
            out _)
            ? installation
            : null;
    }

    private static bool HasDshShim(string path) =>
        File.Exists(Path.Combine(path, "dsh.cmd")) ||
        File.Exists(Path.Combine(path, "dsh.ps1")) ||
        File.Exists(Path.Combine(path, "dsh.exe"));

    private static IEnumerable<string> GetPathEntries()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
    }

    private static string NormalizeDirectory(string value) => value.Trim().Trim('"');

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

    private static string? TryGetNpmGlobalRoot(IReadOnlyCollection<string> pathEntries)
    {
        var node = FindNodeExecutable(pathEntries);
        if (node is null)
        {
            return null;
        }

        var nodeDirectory = Path.GetDirectoryName(node)!;
        var npmCliCandidates = pathEntries
            .Append(nodeDirectory)
            .Select(NormalizeDirectory)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => Path.Combine(path, "node_modules", "npm", "bin", "npm-cli.js"));
        var npmCli = npmCliCandidates.FirstOrDefault(File.Exists);
        if (npmCli is null)
        {
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo(node)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(npmCli);
            startInfo.ArgumentList.Add("root");
            startInfo.ArgumentList.Add("--global");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            return process.ExitCode == 0 && Directory.Exists(output) ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
