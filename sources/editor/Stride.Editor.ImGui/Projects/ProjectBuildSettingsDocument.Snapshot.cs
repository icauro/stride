using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class ProjectBuildSettingsDocument
{
    public AuthoredProjectSnapshot CaptureProjectSnapshot()
    {
        ValidateProjectFileBaseline();
        return new(Path, properties.Snapshot, revision, savedDiskHash.ToArray());
    }
}
