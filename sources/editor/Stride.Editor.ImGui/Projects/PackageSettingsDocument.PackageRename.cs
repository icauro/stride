using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class PackageSettingsDocument
{
    internal byte[]? PackageRenameDiskHash => savedDiskHash?.ToArray();
    internal void RetargetOwnedPackageSettings()
    {
        using var saving = PackageUserSettings.SettingsContainer.BeginGuardedProfileSave(package.UserSettings.Profile);
        PackageUserSettings.SettingsContainer.AcknowledgeSavedSettingsProfile(package.UserSettings.Profile, Path, savedDiskHash ?? []);
        authoringStamp++;
    }
}
