namespace DshDesk.Models;

public enum DshInstallationMode
{
    AutoDetect,
    SpecifiedPath
}

public sealed class DshSettings
{
    public DshInstallationMode InstallationMode { get; set; } = DshInstallationMode.AutoDetect;

    public string DshPackageDirectory { get; set; } = string.Empty;

    public string WorkspaceDirectory { get; set; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public bool CloseToTray { get; set; } = true;

    public int AttachPort { get; set; } = 3080;

    public int StartupTimeoutSeconds { get; set; } = 60;
}
