namespace DshDesk.Services;

public sealed class DshInstallationNotFoundException : InvalidOperationException
{
    public DshInstallationNotFoundException(string message) : base(message)
    {
    }
}
