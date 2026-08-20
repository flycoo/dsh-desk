using DshDesk.Models;

namespace DshDesk.Services;

public static class DshInstallationStatusText
{
    public static string FormatRunningStatus(DshInstallation installation) =>
        $"DSH {installation.Version} · 运行中";

    public static string FormatRunningDetails(DshInstallation installation)
    {
        var source = installation.Source == DshInstallationSource.System
            ? "系统安装"
            : "指定路径";
        return $"版本：{installation.Version} · 启动来源：{source}";
    }

    public static string FormatSystemDetection(
        DshInstallation? detectedInstallation,
        DshInstallation? runningInstallation,
        string installCommand)
    {
        if (detectedInstallation is not null)
        {
            return $"版本：{detectedInstallation.Version}{Environment.NewLine}" +
                   detectedInstallation.PackageDirectory;
        }

        if (runningInstallation?.Source == DshInstallationSource.System)
        {
            return $"当前仍在运行 DSH {runningInstallation.Version}，但其系统安装已不可用。" +
                   $"{Environment.NewLine}重新连接前请先执行：{installCommand}";
        }

        return $"未检测到系统安装。可先执行：{installCommand}";
    }
}
