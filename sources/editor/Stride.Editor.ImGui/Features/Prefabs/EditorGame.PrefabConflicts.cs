using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void ReportPrefabConflict(EditorHierarchyGraphs.Conflict conflict)
        => outputLog.Append("Project", LogMessageType.Error, "Prefab", conflict.Message, conflict.Exception, assetId: conflict.Target);
    private void DrawPrefabConflictActions(AssetId id)
    {
        var graphs = SessionAuthoringHistory?.HierarchyGraphs;
        if (graphs == null || !graphs.TryGetConflict(id, out _)) return;
        Gui.BeginDisabled(IsAuthoringPipelineUpdating || IsProjectLoading || !graphs.CanRetryConflict(id));
        if (Gui.Button("Retry prefab update")) Execute(() => graphs.RetryConflict(id));
        Gui.EndDisabled(); Gui.SameLine();
        Gui.BeginDisabled(IsAuthoringPipelineUpdating || IsProjectLoading);
        if (Gui.Button(EditorLocalization.Widget("Reload project", "Menu"))) RequestReloadProject();
        Gui.EndDisabled();
    }
    private void RequestReloadProject()
    {
        if (workspace == null || IsProjectLoading || IsAuthoringPipelineUpdating || IsUserProjectBuilding) return;
        var path = workspace.OpenPath; var selected = workspace.BuildProjectPath;
        RequestReplace(() => StartOpenProject(path, selected, CaptureProjectReloadViews()), true, true);
    }
}
