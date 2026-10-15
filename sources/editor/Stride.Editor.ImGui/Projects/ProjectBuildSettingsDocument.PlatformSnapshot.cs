namespace Stride.Editor.ImGui;

internal sealed partial class ProjectBuildSettingsDocument
{
    internal byte[] CapturePlatformUpdateProject()
    { CommitEdits(); ValidateProjectFileBaseline(); return properties.Serialize(); }
}
