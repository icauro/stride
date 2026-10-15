namespace Stride.Editor.ImGui;

internal readonly record struct EditorPanelIdentity(string CaptureId, string Title, string WindowName, string Kind = "panel")
{
    internal string DisplayWindowName => CaptureId is "editor.document" or "sprites.editor"
        ? WindowName : EditorLocalization.Widget(WindowName, "View");
}

/// <summary>Capture identity does not depend on the displayed caption.</summary>
internal static class EditorPanels
{
    internal static readonly EditorPanelIdentity Hierarchy = new("scene.hierarchy", "Hierarchy", "Hierarchy");
    internal static readonly EditorPanelIdentity PropertyGrid = new("editor.properties", "Property grid", "Property grid###Inspector");
    internal static readonly EditorPanelIdentity Assets = new("assets.browser", "Asset view", "Asset view");
    internal static readonly EditorPanelIdentity SolutionExplorer = new("assets.folders", "Solution explorer", "Solution explorer");
    internal static readonly EditorPanelIdentity Output = new("editor.output", "Output", "Output");
    internal static readonly EditorPanelIdentity References = new("assets.references", "References", "References");
    internal static readonly EditorPanelIdentity AssetErrors = new("assets.errors", "Asset errors", "###Asset errors");
    internal static readonly EditorPanelIdentity Preview = new("assets.preview", "Asset preview", "Asset preview");
    internal static readonly EditorPanelIdentity History = new("editor.history", "Edit history", "Edit history");
    internal static readonly EditorPanelIdentity Documents = new("editor.document", "Document", "Scene", "window");
    internal static readonly EditorPanelIdentity Sprites = new("sprites.editor", "Sprite Editor", "Sprite Editor", "window");
    internal static readonly EditorPanelIdentity[] All = [Hierarchy, PropertyGrid, Assets, SolutionExplorer, Output, References, AssetErrors, Preview, History, Documents, Sprites];
}
