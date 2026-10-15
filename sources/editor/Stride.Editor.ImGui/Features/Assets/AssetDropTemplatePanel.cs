using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed class AssetDropTemplatePanel
{
    internal sealed record Entry(AssetCreationPanel.Choice Choice, string[] Files);
    internal Action<IDisposable>? RetireProviderLease { get; set; }
    private readonly List<EditorAssetTemplateAdapterLease> failedProviderCleanup = [];
    private bool choosing;
    private Entry[] entries = [];
    private Action<Entry>? choose;
    internal bool HasRequest => choose != null || failedProviderCleanup.Count != 0;
    private bool open, visible;
    private int selected, total;

    internal void Open(Entry[] values, int totalFiles, Action<Entry> select)
    {
        try { Clear(); entries = values; total = totalFiles; choose = select; selected = 0; open = visible = true; }
        catch { Release(values); throw; }
    }

    internal void Draw(Action<Action> execute, Action<Entry, int, System.Numerics.Vector2, System.Numerics.Vector2>? drawTemplate = null)
    {
        DrainFailedProviders();
        var title = EditorLocalization.Widget("Add asset...###DroppedAssetTemplates");
        if (open) { Gui.OpenPopup(title); open = false; }
        if (!Gui.BeginPopupModal(title, ref visible, ImGuiWindowFlags.AlwaysAutoResize))
        { if (!visible) Clear(); return; }
        try
        {
        Entry? picked = null;
        var scale = Gui.GetStyle().FontScaleDpi;
        var height = Math.Min(Math.Max(1, entries.Length) * 56 * scale + Gui.GetStyle().WindowPadding.Y * 2,
            Math.Max(1, Gui.GetMainViewport().WorkSize.Y - Gui.GetFrameHeight() - Gui.GetStyle().WindowPadding.Y * 2));
        using (var child = ImGuiScopes.Child("##Templates", new System.Numerics.Vector2(500 * scale, height), ImGuiChildFlags.Borders, ImGuiWindowFlags.None))
            if (child.DrawContents)
                for (var index = 0; index < entries.Length; index++)
                {
                    var entry = entries[index]; var origin = Gui.GetCursorScreenPos();
                    Gui.PushID(entry.Choice.Template!.Id.ToString());
                    if (Gui.Selectable("##Template", index == selected, ImGuiSelectableFlags.None,
                        new System.Numerics.Vector2(0, 56 * Gui.GetStyle().FontScaleDpi)))
                    { selected = index; picked = entry; }
                    if (!Gui.IsItemVisible()) { Gui.PopID(); continue; }
                    if (drawTemplate != null)
                    {
                        drawTemplate(entry, total, origin, Gui.GetItemRectMax());
                        Gui.PopID(); continue;
                    }
                    var draw = Gui.GetWindowDrawList();
                    draw.AddText(origin, Gui.GetColorU32(ImGuiCol.Text), entry.Choice.Name);
                    draw.AddText(origin + new System.Numerics.Vector2(0, Gui.GetTextLineHeightWithSpacing()), Gui.GetColorU32(ImGuiCol.TextDisabled), entry.Choice.Template.Description ?? "");
                    if (total > 0) draw.AddText(origin + new System.Numerics.Vector2(0, Gui.GetTextLineHeightWithSpacing() * 2), Gui.GetColorU32(ImGuiCol.TextDisabled),
                        entry.Files.Length + " " + string.Format(EditorLocalization.Text("out of {0} file(s)"), total));
                    Gui.PopID();
                }
        var ownsKeys = !Gui.GetIO().AppFocusLost && Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
            && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId) && !Gui.IsAnyItemActive() && !Gui.GetIO().WantTextInput
            && !Gui.GetIO().KeyCtrl && !Gui.GetIO().KeyAlt && !Gui.GetIO().KeySuper && !Gui.GetIO().KeyShift;
        if (ownsKeys && (Gui.IsKeyPressed(ImGuiKey.Enter, false) || Gui.IsKeyPressed(ImGuiKey.KeypadEnter, false)) && entries.Length > 0)
        {
            ImGuiP.SetKeyOwner(ImGuiKey.Enter, Gui.GetID("DroppedTemplateValidate"), (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockThisFrame);
            ImGuiP.SetKeyOwner(ImGuiKey.KeypadEnter, Gui.GetID("DroppedTemplateValidate"), (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockThisFrame);
            picked = entries[selected];
        }
        if (ownsKeys && Gui.IsKeyPressed(ImGuiKey.Escape, false))
        {
            ImGuiP.SetKeyOwner(ImGuiKey.Escape, Gui.GetID("DroppedTemplateCancel"), (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockThisFrame);
            Gui.CloseCurrentPopup(); Clear(); return;
        }
        if (picked != null && choose != null && !choosing && entries.Contains(picked))
        {
            var action = choose;
            var chosen = picked;
            var others = entries.Where(entry => !ReferenceEquals(entry, chosen)).ToArray();
            entries = []; choose = null; open = visible = false; choosing = true; Gui.CloseCurrentPopup();
            var handedOff = false;
            try
            {
                Release(others);
                execute(() =>
                {
                    if (handedOff) return;
                    // The domain dispatcher owns the chosen lease on entry, including failure compensation.
                    handedOff = true; action(chosen);
                });
            }
            finally
            {
                try { if (!handedOff) Release([chosen]); }
                finally { choosing = false; }
            }
        }
        }
        finally { Gui.EndPopup(); }
    }

    private void Release(IEnumerable<Entry> values)
    {
        List<Exception>? errors = null;
        foreach (var entry in values)
            try
            {
                if (entry.Choice.Adapter is { } lease)
                {
                    if (RetireProviderLease != null) RetireProviderLease(lease);
                    else lease.Dispose();
                }
            }
            catch (Exception error)
            {
                if (entry.Choice.Adapter is { } failed && !failedProviderCleanup.Contains(failed)) failedProviderCleanup.Add(failed);
                (errors ??= []).Add(error);
            }
        if (errors != null) throw new AggregateException("Template provider cleanup failed.", errors);
    }

    private void DrainFailedProviders()
    {
        foreach (var lease in failedProviderCleanup.ToArray())
        {
            if (RetireProviderLease != null) RetireProviderLease(lease); else lease.Dispose();
            failedProviderCleanup.Remove(lease);
        }
    }

    internal void Clear()
    {
        var previous = entries; entries = []; choose = null; open = visible = false;
        Release(previous); DrainFailedProviders();
    }
}
