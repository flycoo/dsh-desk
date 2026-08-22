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

    public WindowPlacementSettings? WindowPlacement { get; set; }

    public DateTimeOffset? LastUpdateCheckUtc { get; set; }
}

public sealed class WindowPlacementSettings
{
    public int Left { get; set; }

    public int Top { get; set; }

    public int Right { get; set; }

    public int Bottom { get; set; }

    public bool Maximized { get; set; }
}
