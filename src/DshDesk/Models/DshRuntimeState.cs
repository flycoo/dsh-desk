namespace DshDesk.Models;

public enum DshRuntimeState
{
    Stopped,
    Checking,
    Starting,
    Ready,
    Attached,
    Faulted
}

public sealed record DshStateChangedEventArgs(
    DshRuntimeState State,
    string Message,
    Uri? Url = null,
    bool IsOwned = false);

public enum DshInstallationSource
{
    System,
    Specified
}

public enum ExistingDshChoice
{
    ConnectExisting,
    LaunchSpecified,
    Cancel
}

public sealed record DshInstallation(
    DshInstallationSource Source,
    string Version,
    string PackageDirectory,
    string EntryPoint);
