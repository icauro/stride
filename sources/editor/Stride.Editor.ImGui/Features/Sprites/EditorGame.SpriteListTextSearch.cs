using System.Globalization;
using System.Text;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static readonly CultureInfo SpriteListSearchCulture = CultureInfo.GetCultureInfo("en-US");
    private unsafe void PumpSpriteListTextSearch(AssetMetadataDocument document, SpriteFrameListState state)
    {
        var io = Gui.GetIO(); var now = Gui.GetTime(); state.Search.Expire(now);
        var rows = document.SpriteSettingsFrames;
        if (state.SearchMatch is { } previous)
        {
            var index = -1;
            for (var row = 0; row < rows.Count; row++) if (rows[row].Id == previous) { index = row; break; }
            if (index < 0) { state.Search.Reset(); state.SearchMatch = null; }
            else state.Search.MatchedIndex = index;
        }
        var timeout = WindowsListInputTiming.SearchTimeoutSeconds;
        if (Gui.IsKeyPressed(ImGuiKey.Backspace)) state.Search.Backspace(now, timeout);
        for (var index = 0; index < io.InputQueueCharacters.Size; index++)
        {
            var scalar = (int)io.InputQueueCharacters[index];
            if (!Rune.TryCreate(scalar, out var rune) || Rune.IsControl(rune)) continue;
            if (scalar == ' ' && state.Search.Prefix.Length == 0) continue;
            var match = state.Search.Search(rune.ToString(), rows.Count, row => rows[row].Value.Name ?? "",
                now, timeout, SpriteListSearchCulture);
            if (match < 0) continue;
            var id = rows[match].Id; state.SearchMatch = id;
            if (state.Search.ShouldNavigate)
            {
                state.KeyboardFocus = id; state.ApplyFocus = state.ScrollFocus = true;
                // Native TextSearch navigation deliberately uses no modifiers.
                Execute(() => SelectSpriteFrameAfterPendingEdits(document, id));
            }
        }
    }
}
