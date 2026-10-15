using System.Runtime.CompilerServices;
using Hexa.NET.ImGui;
using Stride.Assets.Sprite;
using Stride.Core.Reflection;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class SpriteFrameListState
    {
        internal ItemId? Editing;
        internal SpriteSettingField? Field;
        internal uint NameInputId;
        internal bool Focus;
        internal NVector2 Scroll;
        internal ItemId? PointerDown;
        internal bool PointerDragged;
        internal ItemId? KeyboardFocus;
        internal bool ScrollFocus, ApplyFocus;
        internal int RenameScrollFrame = -1;
        internal readonly NativeListTextSearch Search = new();
        internal ItemId? SearchMatch;
    }
    private readonly ConditionalWeakTable<AssetMetadataDocument, SpriteFrameListState> spriteFrameLists = new();
    private void BeginSpriteRowRename(AssetMetadataDocument document, SpriteSettingFrame frame)
    {
        var field = frame.Fields.FirstOrDefault(member => member.Node.Name == nameof(SpriteInfo.Name));
        if (field == null || !document.CanEditSpriteSetting(field)) return;
        var state = spriteFrameLists.GetOrCreateValue(document);
        UseTextureSettingsCommandContext();
        if (!Services.Commands.TryExecute(ValidatePendingPropertyStrings) || !IsSpriteSettingDraftCurrent(document, field)) return;
        state.Editing = frame.Id; state.Field = field; state.NameInputId = 0;
        state.Focus = true; state.Scroll = new(Gui.GetScrollX(), Gui.GetScrollY());
    }
    private bool IsSpriteRowRenaming(AssetMetadataDocument document, SpriteSettingFrame frame)
        => spriteFrameLists.TryGetValue(document, out var state) && state.Editing == frame.Id;
    private void HandleSpriteFramePointerSelection(AssetMetadataDocument document, SpriteSettingFrame frame, bool hovered)
    {
        var state = spriteFrameLists.GetOrCreateValue(document);
        if (IsSpriteRowRenaming(document, frame)) return;
        if (hovered && Gui.IsMouseClicked(ImGuiMouseButton.Left))
        { state.PointerDown = frame.Id; state.PointerDragged = false; }
        if (state.PointerDown != frame.Id) return;
        if (Gui.IsMouseDragging(ImGuiMouseButton.Left) || activeSpriteFrameDrag is { } drag && ReferenceEquals(drag.Document, document))
            state.PointerDragged = true;
        if (!hovered || !Gui.IsMouseReleased(ImGuiMouseButton.Left) || state.PointerDragged) return;
        var io = Gui.GetIO();
        state.KeyboardFocus = frame.Id;
        Execute(() => SelectSpriteFrameAfterPendingEdits(document, frame.Id, io.KeyCtrl, io.KeyShift));
    }
    private void CompleteSpriteFramePointerFrame(AssetMetadataDocument document)
    {
        if (!spriteFrameLists.TryGetValue(document, out var state)) return;
        if (!Gui.IsMouseDown(ImGuiMouseButton.Left) || !Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
        { state.PointerDown = null; state.PointerDragged = false; }
    }
    private void DrawSpriteFrameListLabel(AssetMetadataDocument document, SpriteSettingFrame frame, int index)
    {
        if (!spriteFrameLists.TryGetValue(document, out var state) || state.Editing != frame.Id)
        { DrawNativeLabel($"[{index}] {frame.Value.Name ?? ""}"); return; }
        var scroll = new NVector2(Gui.GetScrollX(), Gui.GetScrollY());
        if (Gui.GetFrameCount() <= state.RenameScrollFrame) state.Scroll = scroll;
        if (state.Field == null || !IsSpriteSettingDraftCurrent(document, state.Field) || !document.CanEditSpriteSetting(state.Field)
            || Math.Abs(scroll.X - state.Scroll.X) > .001f || Math.Abs(scroll.Y - state.Scroll.Y) > .001f)
        {
            FinishSpriteRowRename(document, state, false); DrawNativeLabel($"[{index}] {frame.Value.Name ?? ""}"); return;
        }
        if (state.Focus) { Gui.SetKeyboardFocusHere(); state.Focus = false; }
        Gui.SetNextItemWidth(128 * Gui.GetStyle().FontScaleDpi);
        state.NameInputId = Gui.GetID("##SpriteName");
        DrawSpriteNameDraft(document, state.Field, "##SpriteName", true);
        if ((Gui.IsItemActive() || Gui.IsItemDeactivated()) && Gui.IsKeyPressed(ImGuiKey.Escape)) FinishSpriteRowRename(document, state, true);
        else if (Gui.IsItemDeactivated() || Gui.IsItemActive() && Gui.IsKeyPressed(ImGuiKey.Enter)) FinishSpriteRowRename(document, state, false);
    }
    private void FinishSpriteRowRename(AssetMetadataDocument document, SpriteFrameListState state, bool cancel)
    {
        if (state.Field != null && state.NameInputId != 0 && !Services.Commands.TryExecute(() =>
            {
                if (cancel) PropertyGrid.Strings.Abandon(state.Field.Node, state.NameInputId);
                else PropertyGrid.Strings.ValidatePendingForIdentity(state.Field.Node);
            })) return;
        if (state.Field != null && document.IsEditingSpriteSetting(state.Field.Node))
        {
            Execute(cancel ? document.CancelSpriteSettingEdit : document.EndSpriteSettingEdit);
            if (document.IsEditingSpriteSetting(state.Field.Node)) return;
        }
        state.Editing = null; state.Field = null; state.Focus = false; state.NameInputId = 0;
    }
    private void ReleaseSpriteFrameList(AssetMetadataDocument document)
    {
        if (!spriteFrameLists.TryGetValue(document, out var state)) return;
        FinishSpriteRowRename(document, state, false);
        if (state.Editing != null) throw new InvalidOperationException("The sprite name edit could not be completed.");
        spriteFrameLists.Remove(document);
    }
    private Action? DrawSpriteFrameContextMenu(AssetMetadataDocument document, SpriteSettingFrame frame)
    {
        if (!Gui.BeginPopupContextItem("SpriteActions")) return null;
        Action? action = null;
        try
        {
            var io = Gui.GetIO();
            if (!io.KeyCtrl && !io.KeyShift)
            {
                if (!document.IsSpriteSelected(frame.Id)) Execute(() => SelectSpriteFrameAfterPendingEdits(document, frame.Id));
                else document.SetSpriteSelectionAnchor(frame.Id);
                spriteFrameLists.GetOrCreateValue(document).KeyboardFocus = frame.Id;
            }
            if (DrawSpriteImageMenuItem("sprite-add", "Add new sprite", string.Empty, document.CanEditSpriteCollection)) action = document.AddSpriteFrame;
            if (DrawSpriteImageMenuItem("sprite-duplicate", "Duplicate sprite", string.Empty, document.CanEditSpriteCollection && document.SelectedSpriteIds.Count > 0)) action = document.DuplicateSelectedSpriteFrames;
            if (DrawSpriteImageMenuItem("sprite-source-edit", "Edit image")) action = () => EditSpriteSourceImage(document, frame, false);
            if (DrawSpriteImageMenuItem("sprite-source-explore", "Show image in explorer")) action = () => EditSpriteSourceImage(document, frame, true);
            Gui.Separator();
            if (DrawSpriteImageMenuItem("sprite-copy", "Copy", "Ctrl+C", document.SelectedSpriteIds.Count > 0)) action = () => CopySpriteFrames(document);
            if (DrawSpriteImageMenuItem("sprite-paste", "Paste", "Ctrl+V", CanPasteSprites(document))) action = () => PasteSpriteFrames(document);
            if (DrawSpriteImageMenuItem("sprite-remove", "Delete", "Delete", document.CanEditSpriteCollection && document.SelectedSpriteIds.Count > 0)) action = document.DeleteSelectedSpriteFrames;
        }
        finally { Gui.EndPopup(); }
        return action;
    }
    private bool DrawSpriteImageMenuItem(string image, string caption, string shortcut = "", bool enabled = true)
    {
        var style = Gui.GetStyle(); var imageSize = 16 * style.FontScaleDpi;
        var label = EditorLocalization.Text(caption, "Menu");
        var spaces = Math.Max(1, (int)Math.Ceiling((imageSize + style.ItemInnerSpacing.X) / Math.Max(1, Gui.CalcTextSize(" ").X)));
        var selected = Gui.MenuItem(new string(' ', spaces) + label + "###SpriteMenu" + caption, shortcut, false, enabled);
        var minimum = Gui.GetItemRectMin(); var maximum = Gui.GetItemRectMax();
        var origin = new NVector2(minimum.X + style.FramePadding.X, minimum.Y + Math.Max(0, (maximum.Y - minimum.Y - imageSize) * .5f));
        Gui.GetWindowDrawList().AddImage(icons.Get(image), origin, origin + new NVector2(imageSize), NVector2.Zero, NVector2.One,
            Gui.GetColorU32(new System.Numerics.Vector4(1, 1, 1, enabled ? 1 : style.DisabledAlpha)));
        return selected;
    }
    private void DrawSpriteFrameToolbar(AssetMetadataDocument document)
    {
        var first = true; var width = 22 * Gui.GetStyle().FontScaleDpi;
        using var padding = ImGuiScopes.Style(ImGuiStyleVar.FramePadding, new NVector2(3 * Gui.GetStyle().FontScaleDpi));
        bool Button(string image, string tooltip, bool enabled)
        {
            var right = Gui.GetCursorScreenPos().X + Gui.GetContentRegionAvail().X;
            if (!first && Gui.GetItemRectMax().X + Gui.GetStyle().ItemSpacing.X + width <= right) Gui.SameLine();
            first = false; using var disabled = ImGuiScopes.Disabled(!enabled);
            return icons.Button(image, EditorLocalization.Text(tooltip, "ToolTip"), 16);
        }
        if (Button("sprite-add", "Add a new empty sprite", document.CanEditSpriteCollection)) { UseTextureSettingsCommandContext(); Execute(() => { ValidatePendingPropertyStrings(); document.AddSpriteFrame(); }); }
        var selected = document.CanEditSpriteCollection && document.SelectedSpriteIds.Count > 0;
        if (Button("sprite-duplicate", "Duplicate selected sprites", selected)) { UseTextureSettingsCommandContext(); Execute(() => { ValidatePendingPropertyStrings(); document.DuplicateSelectedSpriteFrames(); }); }
        if (Button("sprite-remove", "Delete selected sprites", selected)) { UseTextureSettingsCommandContext(); Execute(() => { ValidatePendingPropertyStrings(); document.DeleteSelectedSpriteFrames(); }); }
        if (Button("sprite-up", "Move selected sprites up", selected)) { UseTextureSettingsCommandContext(); Execute(() => { ValidatePendingPropertyStrings(); document.MoveSelectedSpriteFrames(-1); }); }
        if (Button("sprite-down", "Move selected sprites down", selected)) { UseTextureSettingsCommandContext(); Execute(() => { ValidatePendingPropertyStrings(); document.MoveSelectedSpriteFrames(1); }); }
    }
    private void DrawSpriteSheetType(AssetMetadataDocument document)
    {
        DrawNativeLabel(EditorLocalization.Text("Sheet type:"));
        var field = document.SpriteSettingsFields.First(member => member.Node.Name == nameof(SpriteSheetAsset.Type));
        Gui.BeginDisabled(!document.CanEditSpriteSetting(field));
        for (var index = 0; index < field.EnumNames.Length; index++)
        {
            if (index > 0) Gui.SameLine();
            var value = Enum.Parse(field.Type, field.EnumNames[index]);
            if (Gui.RadioButton(Stride.Core.Presentation.Core.Utils.SplitCamelCase(field.EnumNames[index]) + "###SpriteSheetType" + index, Equals(field.Node.Retrieve(), value)))
            { UseTextureSettingsCommandContext(); Execute(() => { ValidatePendingPropertyStrings(); document.UpdateSpriteSetting(field, value); document.EndSpriteSettingEdit(); }); }
        }
        Gui.EndDisabled();
    }
}
