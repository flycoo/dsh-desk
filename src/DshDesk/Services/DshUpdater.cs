using System.Diagnostics;
using DshDesk.Models;

namespace DshDesk.Services;

public sealed class DshUpdater
{
    private readonly DshSettings _settings;
    private readonly LogService _log;

    public DshUpdater(DshSettings settings, LogService log)
    {
        _settings = settings;
        _log = log;
    }

    public async Task<string> UpdateOfficialPackageAsync(CancellationToken cancellationToken = default)
    {
        var node = DshPackageLocator.FindNodeExecutable()
            ?? throw new InvalidOperationException("未找到 Node.js。");
        var npxCli = FindNpxCli(node)
            ?? throw new InvalidOperationException("未找到 npm/npx。请修复 Node.js 的 npm 安装。");

        var startInfo = new ProcessStartInfo(node)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Directory.GetParent(_settings.DshHome)?.FullName ?? _settings.DshHome
        };
        startInfo.ArgumentList.Add(npxCli);
        startInfo.ArgumentList.Add("--yes");
        startInfo.ArgumentList.Add("@deepseek-ai/dsh@latest");
        startInfo.ArgumentList.Add("--version");
        startInfo.Environment["DSH_HOME"] = _settings.DshHome;
        startInfo.Environment["npm_config_cache"] = _settings.NpmCache;

        using var process = new Process { StartInfo = startInfo };
        _log.Info("Updating official @deepseek-ai/dsh package through npx");
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = (await outputTask.ConfigureAwait(false)).Trim();
        var error = (await errorTask.ConfigureAwait(false)).Trim();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"官方 DSH 更新失败（代码 {process.ExitCode}）：{Environment.NewLine}{error}");
        }

        var installation = DshPackageLocator.FindDshInstallation(_settings.NpmCache)
            ?? throw new InvalidOperationException("npm 已完成，但缓存中仍未找到 @deepseek-ai/dsh。");
        _log.Info($"Official DSH package is now {installation.Version}. npx output: {output}");
        return installation.Version;
    }

    private static string? FindNpxCli(string nodeExecutable)
    {
        var nodeDirectory = Path.GetDirectoryName(nodeExecutable)!;
        var candidates = new[]
        {
            Path.Combine(nodeDirectory, "node_modules", "npm", "bin", "npx-cli.js"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "npm",
                "node_modules",
                "npm",
                "bin",
                "npx-cli.js")
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
