using Stride.Assets.Models;
using Stride.Rendering;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record ModelMaterialClipboardTarget(EntityHierarchyDocument Owner, ComponentField Field, int Slot, IModelAsset Model);
    private void DrawModelMaterialContext(IReadOnlyList<ModelMaterialClipboardTarget> targets, IEnumerable<Material?> values)
    {
        if (!Gui.BeginPopupContextItem("MaterialReferenceActions")) return;
        try
        {
            DrawContentReferenceFetchActions(values);
            if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "", false, targets.Count == 1 && targets[0].Owner.CanCopyModelMaterial(targets[0].Field, targets[0].Slot, targets[0].Model)))
                Execute(() => Gui.SetClipboardText(targets[0].Owner.CopyModelMaterial(targets[0].Field, targets[0].Slot, targets[0].Model)));
            var text = Gui.GetClipboardTextS() ?? "";
            var editable = !IsAuthoringPipelineUpdating && !closeRequested && !closePromptOpen && closingDocument == null;
            var paste = editable && targets.Count != 0 && targets.All(target => target.Owner.CanPasteModelMaterial(target.Field, target.Slot, target.Model, text));
            if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "", false, paste)) Queue(false, false);
            if (DrawNativeMenuItem("Replace", "", false, paste)) Queue(true, false);
            if (DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), "", false, editable && targets.Count != 0 && targets.All(target => target.Owner.CanResetModelMaterial(target.Field, target.Slot)))) Queue(false, true);
            void Queue(bool replace, bool reset) => pendingCollectionAction = () => ApplyModelMaterialClipboard(targets, text, replace, reset);
        }
        finally { Gui.EndPopup(); }
    }
    private void ApplyModelMaterialClipboard(IReadOnlyList<ModelMaterialClipboardTarget> targets, string text, bool replace, bool reset)
    {
        if (IsAuthoringPipelineUpdating || closeRequested || closePromptOpen || closingDocument != null)
            throw new InvalidOperationException("The editor cannot edit these material slots now.");
        var history = sessionHistory ?? throw new InvalidOperationException("The material slots have no shared history.");
        var leases = new List<IDisposable>();
        try
        {
            foreach (var owner in targets.Select(target => target.Owner).Distinct())
            {
                var state = history.Registry.Find(owner.Asset.Id);
                if (!ReferenceEquals(state?.Document, owner) || !state.IsPublished || !ReferenceEquals(owner.SessionHistory, history))
                    throw new InvalidOperationException("A selected material owner changed.");
                leases.Add(state.AcquireView()); owner.EndEdits();
            }
            using var capture = history.CaptureOperationsAtomic(() =>
            {
                foreach (var group in targets.GroupBy(target => target.Owner))
                    if (reset) group.Key.ResetModelMaterials(group.Select(target => (target.Field, target.Slot)).ToArray());
                    else group.Key.PasteModelMaterials(group.Select(target => (target.Field, target.Slot, target.Model)).ToArray(), text, replace);
            });
            capture.Publish(history, reset ? "Reset to base value" : replace ? "Replace property" : "Paste property");
        }
        finally
        {
            List<Exception>? failures = null;
            foreach (var lease in leases) try { lease.Dispose(); } catch (Exception error) { packageCommandCleanup.Add(lease.Dispose); (failures ??= []).Add(error); }
            if (failures != null) throw new AggregateException("Material clipboard owner release requires recovery.", failures);
        }
    }
}
