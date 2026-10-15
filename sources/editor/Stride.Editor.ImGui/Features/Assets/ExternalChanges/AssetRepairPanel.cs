using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal interface IAssetRepairHost
{
    IReadOnlyList<AssetRepairContext> AcquireContexts();
    bool AssetExists(AssetId id);
    object? BrowseSource(AssetRepairField field);
    void DrawReference(AssetRepairField field, object? value, Action<object?> commit);
    void DrawSelectableText(string id, string value);
    void Apply(IReadOnlyList<(AssetRepairField Field, object? Value)> changes);
    void ReportFailure(Exception error);
}

/// <summary>Owns staged replacements; actual canonical graphs retain all edit and history authority.</summary>
internal sealed class AssetRepairPanel(IAssetRepairHost host) : IDisposable
{
    private IReadOnlyList<AssetRepairContext> contexts = [];
    private IReadOnlyList<AssetRepairField> fields = [];
    private readonly Dictionary<AssetRepairField, object?> replacements = [];
    private bool sources;
    private bool open;
    private string? error;
    public void Open(bool sourcePaths)
    {
        Dispose(); sources = sourcePaths; error = null;
        try
        {
            contexts = host.AcquireContexts();
            fields = contexts.SelectMany(context => sources ? context.MissingSources() : context.MissingReferences(host.AssetExists)).ToArray();
            open = true;
        }
        catch (Exception failure) { Dispose(); host.ReportFailure(failure); }
    }
    public void Draw()
    {
        var title = sources ? "Repair missing source paths" : "Repair missing references";
        if (open) { Gui.OpenPopup(title); open = false; }
        using var modal = ImGuiScopes.Modal(title, ImGuiWindowFlags.AlwaysAutoResize);
        if (!modal.DrawContents) return;
        foreach (var field in fields)
        {
            Gui.PushID(field.Context.Owner.CanonicalPath + "/" + field.Path);
            host.DrawSelectableText("##Asset", System.IO.Path.GetFileName(field.Context.Owner.Document.Path));
            host.DrawSelectableText("##Property", field.Path);
            var value = replacements.TryGetValue(field, out var replacement) ? replacement : field.Before;
            if (sources)
            {
                host.DrawSelectableText("##Source", value?.ToString() ?? "");
                if (Gui.Button("Browse..."))
                    try { if (host.BrowseSource(field) is { } selected) replacements[field] = selected; }
                    catch (Exception failure) { error = failure.Message; host.ReportFailure(failure); }
            }
            else host.DrawReference(field, value, selected => replacements[field] = selected);
            Gui.PopID();
        }
        if (error != null) host.DrawSelectableText("##Error", error);
        using (var disabled = ImGuiScopes.Disabled(replacements.Count == 0))
            if (Gui.Button("Apply"))
                try
                {
                    host.Apply(replacements.Select(pair => (pair.Key, pair.Value)).ToArray());
                    Gui.CloseCurrentPopup(); Dispose();
                }
                catch (Exception failure) { error = failure.Message; host.ReportFailure(failure); }
        Gui.SameLine();
        if (Gui.Button(EditorLocalization.Widget("Cancel", "Button"))) { Gui.CloseCurrentPopup(); Dispose(); }
    }
    public void Dispose()
    { foreach (var context in contexts) context.Dispose(); contexts = []; fields = []; replacements.Clear(); open = false; }
}
