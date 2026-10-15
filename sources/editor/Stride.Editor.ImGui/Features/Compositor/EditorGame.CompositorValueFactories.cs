using Hexa.NET.ImGui;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool DrawNativeCompositorValueFactory(AssetMetadataDocument source, IReadOnlyList<CompositorProperty> fields,
        CompositorAggregateProperty? aggregate = null)
    {
        if (fields[0].IsNullable || source.CommonCompositorValueNativeEntries(fields) is not { } entries) return false;
        var target = CaptureCompositorDraftTarget(source, fields, aggregate);
        var values = target.Read().Take(target.Fields.Length).ToArray();
        var hasUnknown = values.Any(raw => raw is Stride.Core.Yaml.IUnloadable);
        var mixed = NativeAbstractEntryControl.IsMixed(entries, values);
        var first = fields[0]; var state = compositorEditors.GetValue(source, static _ => new());
        if (!hasUnknown)
        { if (aggregate != null) DrawCompositorAggregateEnabled(source, aggregate); else DrawCompositorEnabled(source, first); }
        var details = hasUnknown ? 0 : Math.Max(aggregate?.Details.Count ?? first.Details.Count, HasCompositorNativeChildren(first) ? 1 : 0);
        var expanded = false;
        if (details > 0)
        {
            Gui.SetNextItemOpen(first.Expansion != Stride.Core.ExpandRule.Never, ImGuiCond.Once);
            expanded = Gui.TreeNodeEx("##Object"); Gui.SameLine();
        }
        if (aggregate != null) DrawCompositorLabel(aggregate); else DrawCompositorLabel(first);
        ShowCompositorDocumentation(first); Gui.SetNextItemWidth(-1);
        using (ImGuiScopes.Disabled(!target.Editable()))
            if (Gui.BeginCombo("##Type", mixed ? DifferentPropertyValues : NativeAbstractEntryControl.Label(entries, values[0])))
            {
                try { NativeAbstractEntryControl.Choices(entries, values[0], entry => ExecuteCompositorCommand(source, () => Apply(entry))); }
                finally { Gui.EndCombo(); }
            }
        if (expanded)
        {
            if (aggregate != null) DrawCompositorAggregateChildren(source, state, aggregate);
            else DrawCompositorPropertyChildren(source, first);
            Gui.TreePop();
        }
        // Inlined native values retain their actual inner editor and child rules.
        if (!hasUnknown)
        {
            if (aggregate?.InlineMember is { } combinedInline) DrawCompositorAggregateProperty(source, state, combinedInline);
            else if (aggregate == null && first.InlineMember is { } inline) DrawCompositorProperty(source, inline);
        }
        return true;
        void Apply(AbstractNodeEntry entry)
        {
            if (!target.Editable() || !source.CanSelectCompositorValueEntry(target.Fields, entry))
                throw new InvalidOperationException("The native compositor factory owner or entry changed.");
            if (target.Fields.All(field => entry.IsMatchingValue(source.CompositorPropertyValue(field)!))) return;
            using var lease = new RetainedPathLease(this, target.Acquire());
            var actual = target.Fields.DistinctBy(field => field.Accessor).ToArray();
            var owners = new List<EditorPresenterTarget>(); EditorPresenterTarget? combined = null; var transferred = false;
            try
            {
                foreach (var field in actual)
                    if (source.AcquireCompositorFactoryTarget(field) is { } owner) owners.Add(owner);
                if (owners.Count == actual.Length)
                {
                    combined = new(owners);
                    var command = NodePresenterCombination.Commands(combined.Presenters).FirstOrDefault(command => command.Name == "CreateNewInstance");
                    if (command != null)
                    {
                        if (combined.Presenters.Any(presenter => presenter.IsReadOnly)) throw new InvalidOperationException("The native factory value is read-only.");
                        StartEditorExtensionCommand(source, combined, command, entry); transferred = true; return;
                    }
                    if (combined.Presenters.Any(presenter => presenter.Commands.Any(candidate => candidate.Name == "CreateNewInstance")))
                        throw new InvalidOperationException("The registered factory command does not support this native combination.");
                }
                else if (owners.SelectMany(owner => owner.Presenters).Any(presenter => presenter.Commands.Any(command => command.Name == "CreateNewInstance")))
                    throw new InvalidOperationException("A selected native factory presenter is no longer available.");
                source.SelectCompositorValueEntry(target.Fields, entry, aggregate);
            }
            finally
            {
                if (!transferred)
                {
                    if (combined != null) Release(combined);
                    else foreach (var owner in owners) Release(owner);
                }
            }
        }
        void Release(EditorPresenterTarget owner)
        {
            try { owner.Dispose(); } catch { packageCommandCleanup.Add(owner.Dispose); throw; }
        }
    }
}
