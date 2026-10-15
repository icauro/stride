using Stride.Assets;

namespace Stride.Editor.ImGui;

internal static class EditorSceneSettings
{
    internal static GameSettingsAsset ResolveCurrentGameSettings(ProjectWorkspace workspace, EditorSessionHistory? history,
        out EditorAuthoringState? owner)
    {
        GameSettingsAsset? fallback = null;
        return ResolveCanonicalGameSettings(ResolveNativeGameSettings(workspace, ref fallback), history, out owner);
    }

    internal static GameSettingsAsset ResolveNativeGameSettings(ProjectWorkspace workspace, ref GameSettingsAsset? fallback)
    {
        var package = workspace.Session.CurrentProject?.Package;
        // Opening a real standalone .sdpkg has no SolutionProject. Its explicitly
        // adopted package still owns the settings; never search unrelated packages.
        if (package == null && workspace.Package is { } active && workspace.Session.Packages.Contains(active))
            package = active;
        return package?.GetGameSettingsAsset() ?? (fallback ??= GameSettingsFactory.Create());
    }

    internal static GameSettingsAsset ResolveCanonicalGameSettings(GameSettingsAsset settings, EditorSessionHistory? history,
        out EditorAuthoringState? owner)
    {
        // The active project (or explicitly adopted standalone package) owns this
        // service, independently of the scene package or global asset enumeration.
        owner = null;
        if (history?.Registry.Find(settings.Id) is { IsPublished: true, Document: IAssetTagDocument document } state
            && document.AuthoredAsset is GameSettingsAsset authored)
        {
            owner = state;
            return authored;
        }
        return settings;
    }
}
