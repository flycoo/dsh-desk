namespace DshDesk.Models;

public sealed class DshSettings
{
    public string DshHome { get; set; } = @"G:\DeepSeekHarness\.dsh-home";

    public string NpmCache { get; set; } = @"G:\DeepSeekHarness\.npm-cache";

    public string AppDataDirectory { get; set; } = @"G:\DeepSeekHarness\.dsh-desk";

    public bool CloseToTray { get; set; } = true;

    public int AttachPort { get; set; } = 3080;

    public int StartupTimeoutSeconds { get; set; } = 60;
}
