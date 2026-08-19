using DshDesk.Models;

namespace DshDesk.Services;

public sealed record DshLaunchDecision(bool ApplyChanges, bool AttachExisting);

public static class DshLaunchPolicy
{
    public static DshLaunchDecision Decide(
        DshInstallationMode mode,
        bool existingServiceHealthy,
        ExistingDshChoice choice = ExistingDshChoice.LaunchSpecified)
    {
        if (mode == DshInstallationMode.AutoDetect)
        {
            return new DshLaunchDecision(true, true);
        }

        if (!existingServiceHealthy)
        {
            return new DshLaunchDecision(true, false);
        }

        return choice switch
        {
            ExistingDshChoice.ConnectExisting => new DshLaunchDecision(true, true),
            ExistingDshChoice.LaunchSpecified => new DshLaunchDecision(true, false),
            _ => new DshLaunchDecision(false, false)
        };
    }
}
