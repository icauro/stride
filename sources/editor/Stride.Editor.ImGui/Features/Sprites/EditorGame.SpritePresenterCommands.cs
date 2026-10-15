using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Assets.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using Stride.Core.IO;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static IReadOnlyList<AbstractNodeEntry>? SpritePresenterEntries(IReadOnlyList<INodePresenter> nodes)
    {
        var sets = nodes.Where(node => node.AttachedProperties.ContainsKey(AbstractNodeEntryData.Key))
            .Select(node => (IReadOnlyList<AbstractNodeEntry>)((IEnumerable<AbstractNodeEntry>)node.AttachedProperties[AbstractNodeEntryData.Key]).ToArray()).ToArray();
        return sets.Length == 0 ? null : NativeAbstractEntries.Combine(sets);
    }
    private void DrawSpritePresenterFactories(AssetMetadataDocument owner, IReadOnlyList<INodePresenter> nodes)
    {
        if (owner.SessionHistory?.Extensions?.HasPrimitiveEditor(nodes[0].Type) == true
            && !nodes.Any(node => owner.ReadSpritePresenter(node) is Stride.Core.Yaml.IUnloadable)
            || SpritePresenterEntries(nodes) is not { } entries) return;
        using var disabled = ImGuiScopes.Disabled(IsAuthoringPipelineUpdating || nodes.Any(node => !owner.CanEditSpritePresenter(node)));
        if (!Gui.BeginCombo("##Factory", NativeAbstractEntryControl.Label(entries, owner.ReadSpritePresenter(nodes[0])))) return;
        try { NativeAbstractEntryControl.Choices(entries, owner.ReadSpritePresenter(nodes[0]), entry => SelectSpritePresenterEntry(owner, nodes, entry)); }
        finally { Gui.EndCombo(); }
    }
    private void SelectSpritePresenterEntry(AssetMetadataDocument owner, IReadOnlyList<INodePresenter> nodes, AbstractNodeEntry entry)
    {
        Execute(() =>
        {
            var expected = owner.CaptureSpritePresenterGroup(nodes);
            bool Current() => !IsAuthoringPipelineUpdating && owner.IsCurrentSpritePresenterGroup(nodes)
                && nodes.All(owner.CanEditSpritePresenter) && expected.SequenceEqual(owner.CaptureSpritePresenterGroup(nodes));
            var supplied = SpritePresenterEntries(nodes);
            if (!Current() || supplied != null && !NativeAbstractEntries.Contains(supplied, entry)) throw new InvalidOperationException("The sprite factory choice changed.");
            if (nodes.All(node => entry.IsMatchingValue(owner.ReadSpritePresenter(node)!))) return;
            ValidateSpritePresenterAction(owner, nodes, ref expected);
            if (!Current() || supplied != null && !NativeAbstractEntries.Contains(SpritePresenterEntries(nodes) ?? [], entry)) throw new InvalidOperationException("The sprite factory choice changed.");
            var target = AcquireSpriteCommandGroup(owner, nodes); var transferred = false;
            try
            {
                var command = NodePresenterCombination.Commands(target.Presenters).FirstOrDefault(command => command.Name == "CreateNewInstance");
                if (command != null) { transferred = true; StartEditorExtensionCommand(owner, target, command, entry); }
                else ApplySpritePresenterGroup(owner, nodes, (_, current) => entry.GenerateValue(current!));
            }
            finally { if (!transferred) ReleaseHierarchyFactoryTarget(target); }
        });
    }
    private EditorPresenterTarget AcquireSpriteCommandGroup(AssetMetadataDocument owner, IReadOnlyList<INodePresenter> nodes)
    {
        var targets = AcquireHierarchyFactoryTargets(nodes.Count, index => owner.AcquireSpritePresenterTarget(nodes[index]));
        return new(targets.Select(target => target!).ToArray());
    }
    private void DrawSpritePresenterCommands(AssetMetadataDocument owner, IReadOnlyList<INodePresenter> nodes)
    {
        var expected = owner.CaptureSpritePresenterGroup(nodes);
        bool Current() => !IsAuthoringPipelineUpdating && IsCurrentMetadataControlOwner(owner) && owner.IsCurrentSpritePresenterGroup(nodes)
            && expected.SequenceEqual(owner.CaptureSpritePresenterGroup(nodes));
        using var held = new RetainedPathLease(this, AcquireSpritePresenterGroup(owner, nodes));
        if (ContentAssetReference.Supports(nodes[0].Type))
            DrawContentReferenceFetchActions(nodes.Select(owner.ReadSpritePresenter));
        if (nodes.Count == 1 && DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "", false, Current()))
            Execute(() =>
            {
                ValidateSpritePresenterAction(owner, nodes, ref expected); if (!Current()) throw new InvalidOperationException("The sprite property changed.");
                Gui.SetClipboardText(nodes[0] is VirtualNodePresenter virtualNode ? owner.CopyVirtualProperty(virtualNode)
                    : owner.CopyAssetPropertyAccessor(nodes[0].GetNodeAccessor()));
            });
        var text = Gui.GetClipboardTextS() ?? "";
        bool CanPaste() => Current() && nodes.All(owner.CanEditSpritePresenter)
            && (nodes.All(node => node is VirtualNodePresenter)
                ? nodes.Cast<VirtualNodePresenter>().All(node => owner.CanPasteVirtualProperty(node, text))
                : owner.CanPasteAssetAccessors(nodes.Select(node => node.GetNodeAccessor()).ToArray(), text));
        if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "", false, CanPaste())) Paste(false);
        if (DrawNativeMenuItem("Replace", "", false, CanPaste())) Paste(true);
        var resetTargets = nodes.Select(node => node.GetNodeAccessor()).ToArray();
        bool CanReset() => Current() && nodes.All(owner.CanEditSpritePresenter) && resetTargets.All(accessor =>
            accessor.Node is IAssetObjectNode collection && !accessor.Index.IsEmpty
                ? owner.CanResetAssetSettingItem(collection, accessor.Index)
                : accessor.Node != null && owner.CanResetAssetSettingNode(accessor.Node));
        if (DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), "", false, CanReset())) Execute(() =>
        {
            ValidateSpritePresenterAction(owner, nodes, ref expected); if (!CanReset()) throw new InvalidOperationException("The inherited sprite property changed.");
            var sources = nodes.Select(owner.SpritePresenterSourceField).ToArray();
            if (sources.Any(field => field != null))
            {
                if (sources.Any(field => field == null)) throw new InvalidOperationException("The sprite source adapter changed.");
                var values = sources.Select(field => ((IAssetNode)field!.Node).BaseNode is IMemberNode inherited ? inherited.Retrieve() as UFile
                    : throw new InvalidOperationException("The sprite source has no base value.")).ToArray();
                QueueSpriteSourceEdit(owner, sources.Select(field => field!).ToArray(), values, true, "Reset to base value", nodes);
            }
            else owner.ApplyInheritedAssetMutation(() =>
            {
                foreach (var accessor in resetTargets)
                    if (accessor.Node is IAssetObjectNode collection && !accessor.Index.IsEmpty) collection.ResetOverrideRecursively(accessor.Index);
                    else ((IAssetNode)accessor.Node!).ResetOverrideRecursively();
            }, "Reset to base value");
        });
        var entries = SpritePresenterEntries(nodes) ?? [];
        foreach (var command in NodePresenterCombination.Commands(nodes))
        {
            var parameters = entries.Where(entry => command.CanExecute(nodes, entry)).ToArray();
            if (parameters.Length != 0)
            {
                if (!BeginNativeMenu(command.Name, Current())) continue;
                try { NativeAbstractEntryControl.Choices(parameters, owner.ReadSpritePresenter(nodes[0]), entry => Run(command, entry)); }
                finally { Gui.EndMenu(); }
            }
            else if (DrawNativeMenuItem(command.Name, "", false, Current() && command.CanExecute(nodes, null))) Run(command, null);
        }
        void Run(INodePresenterCommand command, object? parameter) => Execute(() =>
        {
            if (!Current() || !command.CanExecute(nodes, parameter)) throw new InvalidOperationException("The sprite command changed.");
            ValidateSpritePresenterAction(owner, nodes, ref expected); if (!Current()) throw new InvalidOperationException("The sprite command source changed.");
            var target = AcquireSpriteCommandGroup(owner, nodes);
            StartEditorExtensionCommand(owner, target, command, parameter);
        });
        void Paste(bool replace) => Execute(() =>
        {
            ValidateSpritePresenterAction(owner, nodes, ref expected); if (!CanPaste()) throw new InvalidOperationException("The sprite clipboard target changed.");
            if (nodes.Any(node => owner.SpritePresenterSourceField(node) != null))
                ApplySpritePresenterGroup(owner, nodes, (_, _) => owner.PrepareAssetPropertyValue(text, nodes[0].Type));
            else if (nodes.All(node => node is VirtualNodePresenter))
            {
                using var captured = sessionHistory!.CaptureOperationsAtomic(() =>
                { foreach (var node in nodes.Cast<VirtualNodePresenter>()) owner.PasteVirtualProperty(node, text, replace); });
                captured.Publish(sessionHistory, replace ? "Replace property" : "Paste property");
            }
            else owner.PasteAssetAccessors(nodes.Select(node => node.GetNodeAccessor()).ToArray(), text, replace);
        });
    }
}
