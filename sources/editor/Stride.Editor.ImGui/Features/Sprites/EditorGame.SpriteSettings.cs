using Hexa.NET.ImGui;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawSpriteSettings(AssetMetadataDocument document, bool inPropertyGrid = false)
    {
        if (document.SpriteSettings == null) return;
        document.PrepareSpriteFramePresentations();
        Gui.PushID("SpriteSettings");
        try
        {
        if (inPropertyGrid)
        {
            DrawSpriteSelectedProperties(document);
            return;
        }
        DrawSpriteEditorLayout(document);
        }
        finally { Gui.PopID(); }
    }
    private void DrawSpriteFrameListPanel(AssetMetadataDocument document)
    {
        if (DrawNativeButton(EditorLocalization.Widget("Sprite sheet properties", "Button"), new NVector2(Gui.GetContentRegionAvail().X, 0)))
            Execute(() => SelectSpriteFrameAfterPendingEdits(document, null));
        DrawSpriteSheetType(document);
        DrawSpriteColorKeySettings(document);
        DrawNativeLabel(EditorLocalization.Text("Sprites:"));
        {
            DrawSpriteFrameToolbar(document);
            Action? rowAction = null;
            // At narrow/high-DPI dock sizes the toolbar may fill the visible
            // column. Reserve real frame rows and let the outer column scroll.
            var listAvailable = Gui.GetContentRegionAvail();
            listAvailable.Y = Math.Max(listAvailable.Y, Gui.GetFrameHeightWithSpacing() * 3);
            using (var list = ImGuiScopes.Child("##SpriteFrameList", listAvailable, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar))
            {
            if (!list.DrawContents) return;
            HandleSpriteFrameListKeyboard(document);
            var frames = document.SpriteSettingsFrames;
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames[index]; Gui.PushID(frame.Id.ToString());
                try
                {
                var origin = Gui.GetCursorScreenPos();
                PrepareSpriteFrameKeyboardRow(document, frame);
                var caption = $"[{index}] {frame.Value.Name ?? ""}";
                var naturalWidth = IsSpriteRowRenaming(document, frame) ? 128 * Gui.GetStyle().FontScaleDpi : MeasureNativeLabel(caption).X;
                var width = Math.Max(Gui.GetWindowSize().X - Gui.GetStyle().WindowPadding.X * 2 - Gui.GetStyle().ScrollbarSize,
                    naturalWidth + Gui.GetStyle().FramePadding.X * 2);
                Gui.Selectable("##Sprite", document.IsSpriteSelected(frame.Id), ImGuiSelectableFlags.AllowDoubleClick | ImGuiSelectableFlags.AllowOverlap,
                    new NVector2(width, Gui.GetFrameHeight()));
                if (Controls.Enabled) Controls.Record("sprites/frame/" + index);
                var rowHovered = Gui.IsItemHovered();
                var next = Gui.GetCursorScreenPos();
                ObserveSpriteFrameKeyboardRow(document, frame);
                AddSpriteFileDropTarget(document, index);
                HandleSpriteFramePointerSelection(document, frame, rowHovered);
                DrawSpriteFrameReordering(document, frame);
                if (rowHovered && Gui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) BeginSpriteRowRename(document, frame);
                rowAction ??= DrawSpriteFrameContextMenu(document, frame);
                Gui.SetCursorScreenPos(origin + new NVector2(Gui.GetStyle().FramePadding.X, Gui.GetStyle().FramePadding.Y));
                DrawSpriteFrameListLabel(document, frame, index);
                Gui.SetCursorScreenPos(next);
                }
                finally { Gui.PopID(); }
            }
            var emptyHeight = Math.Max(Gui.GetFrameHeight(), Gui.GetContentRegionAvail().Y);
            Gui.InvisibleButton("##SpriteListEnd", new NVector2(Math.Max(1, Gui.GetContentRegionAvail().X), emptyHeight));
            AddSpriteFileDropTarget(document, -1);
            DrawSpriteFrameReordering(document, null);
            CompleteSpriteFramePointerFrame(document);
            }
            if (rowAction != null) { UseTextureSettingsCommandContext(); Execute(() => { ValidatePendingPropertyStrings(); rowAction(); }); }
            DeliverSpriteFrameReordering(document);
        }
    }
    private void DrawSpriteSelectedProperties(AssetMetadataDocument document)
    {
        if (document.SelectedSpriteIds.Count == 0)
        {
            DrawPresentedSpriteSheetSettings(document);
        }
        else DrawPresentedSpriteFrameSettings(document);
    }
    private void DrawSpriteSetting(AssetMetadataDocument document, SpriteSettingField field)
    {
        if (!document.IsExtensionPropertyVisible(field.Node)) return;
        Gui.PushID(field.Key);
        try
        {
        if (sessionHistory?.Extensions?.HasPrimitiveEditor(field.Type) == true)
        {
            DrawSpritePropertyLabel(document, field);
            TryDrawSpritePrimitive(field.Type, SpritePropertyLabel(document, field), () => ReadSpriteSettingDraft(document, field),
                () => CanEditSpriteSettingDraft(document, field), create =>
                {
                    var value = create(field.Node.Retrieve());
                    if (field.Type == typeof(UFile) && field.Node.Name == nameof(Stride.Assets.Sprite.SpriteInfo.Source)
                        && field.Node.Parent.Retrieve() is Stride.Assets.Sprite.SpriteInfo) QueueSpriteSourceEdit(document, [field], new UFile?[] { value as UFile });
                    else if (value == null) document.ApplyInheritedAssetMutation(() => field.Node.Update(null), "Edit " + field.Label);
                    else CommitSpriteSettingDraft(document, field, value);
                }, () => AcquireSpriteSettingDraft(document, field));
            DrawAssetSettingResetToBase(document, field.Node); return;
        }
        if (field.Type == typeof(UFile)) { DrawSpriteSourceSetting(document, field); return; }
        if (field.Type == typeof(string))
        {
            DrawSpritePropertyLabel(document, field); Gui.SetNextItemWidth(-1);
            DrawSpriteNameDraft(document, field); DrawAssetSettingResetToBase(document, field.Node); return;
        }
        if (AssetSettingNumericMetadata.Supports(field.Type) || field.Type == typeof(Vector2) || field.Type == typeof(Vector4) || field.Type == typeof(Rectangle))
        {
            DrawSpriteNumericSetting(document, field);
            DrawAssetSettingResetToBase(document, field.Node); return;
        }
        if (field.Type == typeof(Color))
        {
            DrawSpritePropertyLabel(document, field);
            DrawSpriteColorDraft(document, field); DrawAssetSettingResetToBase(document, field.Node); return;
        }
        DrawSpritePropertyLabel(document, field); Gui.SetNextItemWidth(-1);
        if (field.Type == typeof(bool) || field.Type.IsEnum) DrawSpriteDiscreteSetting(document, field);
        else DrawSelectableText("##Value", field.Node.Retrieve()?.ToString() ?? "");
        DrawAssetSettingResetToBase(document, field.Node);
        }
        finally { Gui.PopID(); }
    }
}
