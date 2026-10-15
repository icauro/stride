using System.Runtime.CompilerServices;
using Stride.Core.Assets;
using Stride.Core.Extensions;
using Stride.Core.Reflection;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class HierarchyDictionaryAddView { internal string Text = ""; internal uint Input; internal bool Close; }
    private readonly ConditionalWeakTable<object, Dictionary<uint, HierarchyDictionaryAddView>> hierarchyDictionaryAddViews = new();
    private void DrawHierarchyDictionaryDefaultAdd(IReadOnlyList<EntityHierarchyDocument> sourceOwners,
        IReadOnlyList<HierarchyCollectionContext> sourceContexts, CommonComponentProperty? property = null)
    {
        var owners = sourceOwners.ToArray(); var contexts = sourceContexts.ToArray();
        if (contexts.Length == 0 || contexts[0].Descriptor is not DictionaryDescriptor descriptor
            || !AssetCollectionCommandRules.CanConstruct(descriptor.KeyType) || !AssetCollectionCommandRules.CanAdd(descriptor.ValueType)) return;
        bool Current() => !IsAuthoringPipelineUpdating && contexts.Select((context, index) =>
            owners[index].CanAddNestedHierarchyKeys(context) && context.Origin.Component.Entity is { } entity && owners[index].IsSelected(entity.Id)).All(value => value);
        object?[] Certificates()
        {
            var values = owners.Select(owner => (object?)owner.EditRevision).ToList();
            foreach (var context in contexts)
            {
                values.Add(context.Node);
                foreach (var index in context.Node.Indices ?? []) { values.Add(index); values.Add(context.Node.Retrieve(index)); }
                for (var parent = context.Parent; parent != null; parent = parent.Context.Parent)
                { values.Add(parent.Collection); values.Add(parent.Index); values.Add(parent.IdentityKey); }
            }
            return values.ToArray();
        }
        void Add(object? parameter)
        {
            if (!Current()) throw new InvalidOperationException("The dictionary changed before adding the key.");
            SelectHierarchyDictionaryDefaultAdd(owners, contexts, parameter, () =>
            {
                if (property == null) owners[0].AddHierarchyDictionaryDefaultItem(contexts[0], parameter);
                else ApplyAggregateMutation(property, field =>
                {
                    var index = AggregateFieldIndex(property, field);
                    return owners[index].PrepareHierarchyDictionaryDefaultAdd(contexts[index], parameter);
                }, "Add key: " + property.First.Label);
            });

        }
        DrawNativeDictionaryAdd(contexts[0].Node, descriptor, Current, Certificates, Add,
            () => AcquirePropertyStringOwners(owners.Distinct()));
    }
    private void DrawNativeDictionaryAdd(object identity, DictionaryDescriptor descriptor, Func<bool> current,
        Func<object?[]> certificates, Action<object?> assign, Func<IDisposable> acquire)
    {
        if (!AssetCollectionCommandRules.CanConstruct(descriptor.KeyType) || !AssetCollectionCommandRules.CanAdd(descriptor.ValueType)) return;
        bool Current() => !IsAuthoringPipelineUpdating && current();
        var states = hierarchyDictionaryAddViews.GetValue(identity, _ => new()); var id = Gui.GetID("##AddDictionaryKey");
        if (!states.TryGetValue(id, out var view)) states[id] = view = new();
        object?[] Read() => new object?[] { view.Text }.Concat(certificates()).ToArray();
        void Add(object? parameter)
        {
            if (!Current()) throw new InvalidOperationException("The dictionary changed before adding the key.");
            assign(parameter); view.Close = true;
        }
        var popup = descriptor.KeyType == typeof(string) || descriptor.KeyType.IsNumeric() || descriptor.KeyType.IsEnum;
        using var disabled = ImGuiScopes.Disabled(!Current());
        if (Gui.Button("Add")) Execute(() =>
        {
            ValidatePendingPropertyStrings();
            if (!Current()) throw new InvalidOperationException("The dictionary changed before opening.");
            if (!popup) { Add(null); return; }
            view.Text = descriptor.KeyType == typeof(string) ? EditorLocalization.Text("New key") : "0";
            view.Close = false; Gui.OpenPopup("##DictionaryKey");
        });
        if (Gui.BeginPopup("##DictionaryKey"))
        {
            try
            {
                Gui.TextUnformatted(EditorLocalization.Text("Key name:"));
                if (descriptor.KeyType.IsEnum)
                {
                    var options = descriptor.KeyType.IsDefined(typeof(FlagsAttribute), false)
                        ? EditorFlagEnums.GetIndividualFlags(descriptor.KeyType).Cast<Enum>() : Enum.GetValues(descriptor.KeyType).Cast<Enum>().Distinct();
                    foreach (var option in options)
                        if (DrawNativeSelectable(PackageEnumDisplay(option))) Execute(() => Add(option));
                }
                else
                {
                    view.Input = Gui.GetID("##NewDictionaryKey");
                    try { PropertyGrid.Strings.Draw(identity, false, Current, Read, Add,
                        acquire, controlLabel: "##NewDictionaryKey", cancelOnLostFocus: true); }
                    catch (Exception error) { PropertyConversionFeedback.Fail(view.Input, error); statusMessage = error.Message; }
                    PropertyConversionFeedback.Draw(view.Input);
                }
                if (view.Close) Gui.CloseCurrentPopup();
            }
            finally { Gui.EndPopup(); }
        }
        else if (view.Input != 0) { PropertyGrid.Strings.Abandon(identity, view.Input); view.Input = 0; }
    }
}
