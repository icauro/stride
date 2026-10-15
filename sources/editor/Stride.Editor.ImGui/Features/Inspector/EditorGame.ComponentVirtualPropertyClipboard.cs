using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawComponentVirtualClipboard(EntityHierarchyDocument owner, VirtualNodePresenter presenter, ComponentNodePresentation presentation)
        => DrawCombinedComponentVirtualClipboard([new(owner, presentation, presenter)]);
    private void DrawCombinedComponentVirtualClipboard(IReadOnlyList<ComponentVirtualOwner> entries)
    {
        // Original CopyProperty uses DoNotCombine, while paste and reset combine all targets.
        if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "", false, entries.Count == 1 && entries[0].Document.CanCopyComponentVirtual(entries[0].Node)))
            Execute(() => Gui.SetClipboardText(entries[0].Document.CopyComponentVirtual(entries[0].Node)));
        var text = Gui.GetClipboardTextS() ?? "";
        var editable = !IsAuthoringPipelineUpdating && !closeRequested && !closePromptOpen && closingDocument == null;
        var paste = editable && entries.Count != 0 && entries.All(entry => entry.Document.CanPasteComponentVirtual(entry.Node, text));
        if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "", false, paste)) Run(false, false);
        if (DrawNativeMenuItem("Replace", "", false, paste)) Run(true, false);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), "", false, editable && entries.Count != 0 && entries.All(entry => entry.Document.CanResetComponentVirtual(entry.Node)))) Run(false, true);
        void Run(bool replace, bool reset) => pendingCollectionAction = () => ApplyCombinedComponentVirtualClipboard(entries, text, replace, reset);
    }
    private void ApplyCombinedComponentVirtualClipboard(IReadOnlyList<ComponentVirtualOwner> entries, string text, bool replace, bool reset)
    {
        if (IsAuthoringPipelineUpdating || closeRequested || closePromptOpen || closingDocument != null)
            throw new InvalidOperationException("The editor cannot edit these properties now.");
        var history = sessionHistory ?? throw new InvalidOperationException("The selected properties have no shared history.");
        var groups = entries.GroupBy(entry => entry.Document).ToArray();
        var targets = new List<EditorPresenterTarget>();
        var leases = new List<IDisposable>();
        try
        {
            foreach (var group in groups)
            {
                var state = history.Registry.Find(group.Key.Asset.Id);
                if (!ReferenceEquals(state?.Document, group.Key) || !state.IsPublished || !ReferenceEquals(group.Key.SessionHistory, history))
                    throw new InvalidOperationException("A selected component owner changed.");
                leases.Add(state.AcquireView()); group.Key.EndEdits();
            }
            foreach (var entry in entries) targets.Add(entry.Presentation.AcquireVirtualTarget(entry.Node));
            using var captured = history.CaptureOperationsAtomic(() =>
            {
                foreach (var group in groups)
                {
                    var nodes = group.Select(entry => entry.Node).Distinct().ToArray();
                    if (reset) group.Key.ResetComponentVirtual(nodes);
                    else group.Key.PasteComponentVirtual(nodes, text, replace);
                }
            });
            captured.Publish(history, reset ? "Reset to base value" : replace ? "Replace property" : "Paste property");
        }
        finally
        {
            List<Exception>? failures = null;
            foreach (var target in targets) try { target.Dispose(); } catch (Exception error) { packageCommandCleanup.Add(target.Dispose); (failures ??= []).Add(error); }
            foreach (var lease in leases) try { lease.Dispose(); } catch (Exception error) { packageCommandCleanup.Add(lease.Dispose); (failures ??= []).Add(error); }
            if (failures != null) throw new AggregateException("Component clipboard owner release requires recovery.", failures);
        }
    }
}
