using System.Runtime.CompilerServices;
using System.Globalization;
using System.Text;
using Hexa.NET.ImGui;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class MaterialStripSelection
    {
        internal int Selected = -1;
        internal EditorMaterialPrimitive? Actual;
        internal int Focused = -1, LastFrame = -1;
        internal bool ApplyFocus;
        internal uint Capture;
        internal readonly NativeListTextSearch Search = new();
    }
    private readonly ConditionalWeakTable<MaterialAssetPreview, MaterialStripSelection> materialStripSelections = new();
    private static readonly string[] MaterialStripNames = Enum.GetNames<EditorMaterialPrimitive>();
    private static readonly string[] MaterialStripIds = MaterialStripNames.Select(static name => "##MaterialPrimitive" + name).ToArray();

    private void DrawMaterialPreviewStrip(MaterialAssetPreview preview)
    {
        var state = materialStripSelections.GetValue(preview, static _ => new MaterialStripSelection());
        if (state.Actual != preview.Primitive)
        { state.Actual = preview.Primitive; state.Focused = state.Selected = Array.IndexOf(MaterialPreviewPrimitives, preview.Primitive); }
        var frame = Gui.GetFrameCount();
        if (state.LastFrame != frame - 1 || !EditorIconStripInput.IsCaptureActive(state.Capture)) state.Capture = 0;
        state.LastFrame = frame;
        var scale = Gui.GetStyle().FontScaleDpi;
        Gui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        try
        {
            // Original ScrollViewer has both axes Disabled, rather than merely
            // hiding their bars. Keep native navigation from scrolling the strip.
            Gui.SetNextWindowContentSize(new Vector2(Math.Max(1, Gui.GetContentRegionAvail().X), 28 * scale));
            Gui.BeginChild("##MaterialPrimitiveStrip", new Vector2(0, 28 * scale), ImGuiChildFlags.None,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            try
            {
                if (Gui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows)) PumpMaterialStripKeys(preview, state);
                var start = Gui.GetCursorScreenPos() + new Vector2(2 * scale);
                for (var index = 0; index < MaterialPreviewPrimitives.Length; ++index)
                {
                    var primitive = MaterialPreviewPrimitives[index];
                    var origin = start + new Vector2(index * 25 * scale, 0);
                    Gui.SetCursorScreenPos(origin);
                    var id = MaterialStripIds[index];
                    var activated = EditorIconStripInput.Draw(id, state.Selected == index, out var deselect);
                    if (Gui.IsItemClicked(ImGuiMouseButton.Left)) state.Capture = Gui.GetItemID();
                    if (!state.ApplyFocus && Gui.IsItemFocused()) state.Focused = index;
                    if (state.ApplyFocus && state.Focused == index)
                    { EditorIconStripInput.Focus(id); state.ApplyFocus = false; }
                    if (EditorIconStripInput.IsPointerDragTarget(state.Capture))
                    {
                        state.Focused = index; EditorIconStripInput.Focus(id);
                        // Native single-select keyboard/mouse navigation with
                        // Control moves focus while preserving selection.
                        if (!Gui.GetIO().KeyCtrl) { activated = true; deselect = false; }
                    }
                    if (activated)
                    {
                        state.Selected = deselect ? -1 : index;
                        if (!deselect)
                        {
                            Execute(() => preview.SetPrimitive(primitive));
                            state.Actual = preview.Primitive;
                        }
                    }
                    if (Controls.Enabled) Controls.Record("material-preview/primitive/" + primitive);
                    if (state.Selected == index) Gui.SetItemDefaultFocus();
                    if (Gui.IsItemVisible())
                    {
                        // Native 2-DIP container padding, 20-DIP content border
                        // with one trailing DIP, and centered 16-DIP artwork.
                        var image = origin + new Vector2(4 * scale);
                        Gui.GetWindowDrawList().AddImage(icons!.Get(MaterialPreviewPrimitiveIcons[index]),
                            image, image + new Vector2(16 * scale));
                    }
                }
            }
            finally { ImGuiScopes.EndChild(); }
        }
        finally { Gui.PopStyleVar(); }
    }
    private unsafe void PumpMaterialStripKeys(MaterialAssetPreview preview, MaterialStripSelection state)
    {
        var io = Gui.GetIO(); var now = Gui.GetTime(); state.Search.Expire(now);
        var focus = Math.Clamp(state.Focused, 0, MaterialPreviewPrimitives.Length - 1);
        EditorIconStripInput.ClaimSearchKeys(MaterialStripIds[focus]);
        if (!io.KeyAlt && !io.KeySuper && (Gui.IsKeyPressed(ImGuiKey.Home) || Gui.IsKeyPressed(ImGuiKey.End)))
        {
            var index = Gui.IsKeyPressed(ImGuiKey.Home) ? 0 : MaterialPreviewPrimitives.Length - 1;
            state.Focused = index; state.ApplyFocus = true;
            if (!io.KeyCtrl) SelectMaterialStripPrimitive(preview, state, index);
        }
        var timeout = WindowsListInputTiming.SearchTimeoutSeconds;
        if (Gui.IsKeyPressed(ImGuiKey.Backspace)) state.Search.Backspace(now, timeout);
        for (var character = 0; character < io.InputQueueCharacters.Size; ++character)
        {
            if (!Rune.TryCreate((int)io.InputQueueCharacters[character], out var rune) || Rune.IsControl(rune)) continue;
            if (rune.Value == ' ' && state.Search.Prefix.Length == 0) continue;
            var match = state.Search.Search(rune.ToString(), MaterialPreviewPrimitives.Length,
                static index => MaterialStripNames[index], now, timeout, CultureInfo.GetCultureInfo("en-US"));
            if (match < 0 || !state.Search.ShouldNavigate) continue;
            state.Focused = match; state.ApplyFocus = true;
            // Actual WPF TextSearch navigates with ModifierKeys.None.
            SelectMaterialStripPrimitive(preview, state, match);
        }
    }
    private void SelectMaterialStripPrimitive(MaterialAssetPreview preview, MaterialStripSelection state, int index)
    {
        var primitive = MaterialPreviewPrimitives[index];
        Execute(() => preview.SetPrimitive(primitive));
        state.Actual = preview.Primitive; state.Selected = index;
    }
}
