namespace Stride.Editor.ImGui;

internal interface ICatalogAssetPreviewHost
{
    EditorIcons Icons { get; }
    UiControls Controls { get; }
    void ReleasePreview(IDisposable preview, bool immediate);
    void ReportFailure(string kind, Exception error);
    void RegisterCaptureItem(string id, string kind, string name);
    void DrawSelectableText(string id, string text);
    void DrawCameraInput(string id, System.Numerics.Vector2 size, Stride.Engine.AssetPreviewCamera camera);
    bool DrawToolbarIcon(string name, float rowY, float rowHeight);
}
