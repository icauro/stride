using Hexa.NET.ImGui;
using Stride.Core.Reflection;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private const string SpriteFrameDragType = "STRIDE_SPRITE_FRAMES";
    private sealed record SpriteFrameDrag(AssetMetadataDocument Document, ItemId[] Ids, Guid Token);
    private SpriteFrameDrag? activeSpriteFrameDrag;
    private int lastSpriteFrameDrag;
    private (SpriteFrameDrag Drag, ItemId? Target, bool After)? deliveredSpriteFrameDrag;

    private unsafe void DrawSpriteFrameReordering(AssetMetadataDocument document, SpriteSettingFrame? frame)
    {
        if (!document.CanEditSpriteCollection) return;
        if (frame != null && Gui.BeginDragDropSource())
        {
            lastSpriteFrameDrag = frames;
            if (activeSpriteFrameDrag is not { } drag || !ReferenceEquals(drag.Document, document)
                || !drag.Ids.Contains(frame.Id) || !Gui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                if (!document.IsSpriteSelected(frame.Id) && !Services.Commands.TryExecute(() => SelectSpriteFrameAfterPendingEdits(document, frame.Id)))
                { Gui.EndDragDropSource(); return; }
                activeSpriteFrameDrag = new(document, document.SelectedSpriteIds.ToArray(), Guid.NewGuid());
            }
            var token = activeSpriteFrameDrag.Token;
            Gui.SetDragDropPayload(SpriteFrameDragType, &token, (nuint)sizeof(Guid));
            foreach (var row in document.SpriteSettingsFrames)
                if (activeSpriteFrameDrag.Ids.Contains(row.Id)) Gui.TextUnformatted(row.Value.Name ?? "");
            Gui.EndDragDropSource();
        }
        if (!Gui.BeginDragDropTarget()) return;
        var payload = Gui.AcceptDragDropPayload(SpriteFrameDragType, ImGuiDragDropFlags.AcceptBeforeDelivery | ImGuiDragDropFlags.AcceptNoDrawDefaultRect);
        if (!payload.IsNull && payload.DataSize == sizeof(Guid) && activeSpriteFrameDrag is { } source
            && (ReferenceEquals(source.Document, document) ? frame == null || !source.Ids.Contains(frame.Id) : document.CanTransferSpriteFrames(source.Document))
            && *(Guid*)payload.Data == source.Token)
        {
            var minimum = Gui.GetItemRectMin(); var maximum = Gui.GetItemRectMax();
            var after = frame == null || Gui.GetIO().MousePos.Y >= (minimum.Y + maximum.Y) * .5f;
            var y = after ? maximum.Y : minimum.Y;
            Gui.GetWindowDrawList().AddLine(new(minimum.X, y), new(maximum.X, y), Gui.GetColorU32(ImGuiCol.DragDropTarget), 2);
            if (payload.IsDelivery()) deliveredSpriteFrameDrag = (source, frame?.Id, after);
        }
        Gui.EndDragDropTarget();
    }
    private void DeliverSpriteFrameReordering(AssetMetadataDocument document)
    {
        if (deliveredSpriteFrameDrag is not { } delivered) return;
        deliveredSpriteFrameDrag = null; activeSpriteFrameDrag = null;
        UseTextureSettingsCommandContext();
        Execute(() => { ValidatePendingPropertyStrings(); document.TransferSpriteFrames(delivered.Drag.Document, delivered.Drag.Ids, delivered.Target, delivered.After); });
    }
}
