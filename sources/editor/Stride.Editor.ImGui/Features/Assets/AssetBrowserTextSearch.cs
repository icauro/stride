using System.Globalization;
using System.Text;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>Actual AssetView TextSearch over the owning view's visible native rows.</summary>
internal sealed class AssetBrowserTextSearch
{
    private static readonly CultureInfo SearchCulture = CultureInfo.GetCultureInfo("en-US");
    private readonly NativeListTextSearch search = new();
    private string? matchedIdentity;
    internal void Reset() { search.Reset(); matchedIdentity = null; }
    internal unsafe int? ReadTarget<T>(IReadOnlyList<T> rows, Func<T, string> identity, Func<T, string> name, bool allowModalOwner = false)
    {
        var now = Gui.GetTime(); search.Expire(now);
        var io = Gui.GetIO();
        if (!Gui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows) || io.AppFocusLost || io.WantTextInput || Gui.IsAnyItemActive()
            || (ImGuiP.GetItemFlags() & (ImGuiItemFlags)ImGuiItemFlagsPrivate.Disabled) != 0
            || Gui.IsPopupOpen("", allowModalOwner ? ImGuiPopupFlags.AnyPopupId : ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)) return null;
        if (rows.Count == 0) { Reset(); return null; }
        if (matchedIdentity != null)
        {
            var index = search.MatchedIndex;
            if (index < 0 || index >= rows.Count || identity(rows[index]) != matchedIdentity)
            {
                index = -1;
                for (var row = 0; row < rows.Count; row++) if (identity(rows[row]) == matchedIdentity) { index = row; break; }
                if (index < 0) Reset(); else search.MatchedIndex = index;
            }
        }
        // Request committed native text/IME for the actual focused tile viewport.
        // This is not an active editing field: normal command/key ownership remains.
        var context = Gui.GetCurrentContext();
        context.Handle->PlatformImeData.WantVisible = 0;
        context.Handle->PlatformImeData.WantTextInput = 1;
        context.Handle->PlatformImeData.InputPos = Gui.GetCursorScreenPos();
        context.Handle->PlatformImeData.InputLineHeight = Gui.GetTextLineHeight();
        context.Handle->PlatformImeData.ViewportId = Gui.GetWindowViewport().ID;
        if (io.KeySuper || io.KeyCtrl && !io.KeyAlt || io.KeyAlt && !io.KeyCtrl) return null;
        var timeout = WindowsListInputTiming.SearchTimeoutSeconds;
        if (search.Prefix.Length != 0 && Gui.IsKeyPressed(ImGuiKey.Backspace))
        {
            ImGuiP.SetKeyOwner(ImGuiKey.Backspace, Gui.GetID("AssetViewTextSearch"), (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockThisFrame);
            search.Backspace(now, timeout);
        }
        int? target = null;
        for (var character = 0; character < io.InputQueueCharacters.Size; character++)
        {
            if (!Rune.TryCreate((int)io.InputQueueCharacters[character], out var rune) || Rune.IsControl(rune)) continue;
            if (rune.Value == ' ' && search.Prefix.Length == 0) continue;
            var match = search.Search(rune.ToString(), rows.Count, index => name(rows[index]), now, timeout, SearchCulture);
            if (match < 0) continue;
            matchedIdentity = identity(rows[match]);
            if (search.ShouldNavigate) target = match;
        }
        return target;
    }
}
