using System.Globalization;
using System.Runtime.CompilerServices;
using Stride.Core.Assets.Quantum;
using Stride.Core.Reflection;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class DictionaryAddKeyDraft { internal string Text = ""; }
    private readonly ConditionalWeakTable<object, Dictionary<uint, DictionaryAddKeyDraft>> dictionaryAddKeys = new();

    private void DrawAggregateDictionaryKey(CommonComponentProperty property, IReadOnlyList<HierarchyCollectionItem> source)
    {
        var items = source.ToArray(); var fields = property.Fields.ToArray();
        var owners = fields.Select(field => aggregatePropertyOwners[field]).ToArray();
        bool Editable() => !IsAuthoringPipelineUpdating && items.Select((item, index) =>
            aggregatePropertyOwners.TryGetValue(fields[index], out var current) && ReferenceEquals(current, owners[index])
            && owners[index].CanEditNestedHierarchyKeys(item.Context) && owners[index].CanEditHierarchyItem(item.Collection, item.Index)
            && (item.Id == null || item.Collection.HasId(item.Id.Value))).All(editable => editable);
        object?[] Read()
        {
            var baseline = items.Select(item => item.Index.Value).ToList();
            foreach (var item in items)
            {
                baseline.Add(item.Collection); baseline.Add(((DictionaryDescriptor)item.Context.Descriptor).KeyType);
                baseline.Add(item.Id); baseline.Add(item.Value);
                for (var parent = item.Context.Parent; parent != null; parent = parent.Context.Parent)
                { baseline.Add(parent.Collection); baseline.Add(parent.Index); baseline.Add(parent.IdentityKey); }
            }
            return baseline.ToArray();
        }
        var first = items[0]; var key = first.Index.Value;
        var mixed = items.Skip(1).Any(item => !Equals(key, item.Index.Value));
        Gui.SetNextItemWidth(-1); var feedback = Gui.GetID("##Value");
        try
        {
            PropertyGrid.Strings.Draw(first.Collection, mixed, Editable, Read, text =>
                ApplyAggregateMutation(property, field =>
                {
                    var index = Array.IndexOf(fields, field);
                    return PropertyConversionFeedback.Convert(feedback, () => owners[index].PrepareNestedHierarchyRename(items[index], text));
                }, "Rename key: " + property.First.Label), () => AcquirePropertyStringOwners(owners),
                format: value => DictionaryKeyText(value!), refreshAfterCommit: false, cancelOnLostFocus: true,
                onCancelled: error => ClearCancelledPropertyDraft(error, feedback));
        }
        catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); SetPropertyDraftDiagnostic(error); }
        PropertyConversionFeedback.Draw(feedback);
    }

    private DictionaryAddKeyDraft DrawAggregateDictionaryAddKey(CommonComponentProperty property, IReadOnlyList<HierarchyCollectionContext> source)
    {
        var contexts = source.ToArray(); var fields = property.Fields.ToArray();
        var owners = fields.Select(field => aggregatePropertyOwners[field]).ToArray();
        var feedback = Gui.GetID("##Key");
        var states = dictionaryAddKeys.GetValue(contexts[0].Node, _ => new());
        if (!states.TryGetValue(feedback, out var state)) states.Add(feedback, state = new());
        bool Editable() => !IsAuthoringPipelineUpdating && contexts.Select((context, index) =>
            aggregatePropertyOwners.TryGetValue(fields[index], out var current) && ReferenceEquals(current, owners[index])
            && owners[index].CanAddNestedHierarchyKeys(context)).All(editable => editable);
        object?[] Read()
        {
            var baseline = new List<object?> { state.Text };
            for (var index = 0; index < contexts.Length; index++)
            {
                var context = contexts[index]; baseline.Add(context.Node); baseline.Add(((DictionaryDescriptor)context.Descriptor).KeyType);
                foreach (var item in owners[index].GetNestedHierarchyItems(context))
                { baseline.Add(item.Id); baseline.Add(item.Index); }
                for (var parent = context.Parent; parent != null; parent = parent.Context.Parent)
                { baseline.Add(parent.Collection); baseline.Add(parent.Index); baseline.Add(parent.IdentityKey); }
            }
            return baseline.ToArray();
        }
        try
        {
            PropertyGrid.Strings.Draw(contexts[0].Node, false, Editable, Read, text =>
            {
                PropertyConversionFeedback.Run(feedback, () =>
                {
                    for (var index = 0; index < contexts.Length; index++) owners[index].ValidateNestedHierarchyAddKey(contexts[index], text);
                    state.Text = text;
                });
            }, () => AcquirePropertyStringOwners(owners), controlLabel: "##Key",
                onCancelled: error => ClearCancelledPropertyDraft(error, feedback));
        }
        catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); SetPropertyDraftDiagnostic(error); }
        PropertyConversionFeedback.Draw(feedback); return state;
    }
}
