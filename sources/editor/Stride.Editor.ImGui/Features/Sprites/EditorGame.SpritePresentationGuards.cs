namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool CanMutateSpriteCanvasPresentation(AssetMetadataDocument source, SpriteSettingFrame frame, SpriteCanvasState state)
        => CanMutateDocumentPresentation(source) && spriteCanvases.TryGetValue(source, out var current)
            && ReferenceEquals(current, state) && state.FrameId == frame.Id
            && source.SpriteSettingsFrames.Any(item => item.Id == frame.Id && ReferenceEquals(item.Value, frame.Value));
}
