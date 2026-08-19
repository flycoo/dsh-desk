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

public sealed record DshInstallation(
    string Version,
    string PackageDirectory,
    string EntryPoint);
