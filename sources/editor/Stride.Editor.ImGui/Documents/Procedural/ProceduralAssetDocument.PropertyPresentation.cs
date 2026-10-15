using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed partial class ProceduralAssetDocument
{
    private readonly HashSet<IMemberNode> proceduralMembers = [];
    internal bool IsPropertyFieldCurrent(AssetPropertyField field) => proceduralMembers.Contains(field.Node);
    internal bool PropertyVisible(IMemberNode member) => NumericPresentation?.IsVisible(member) != false;
    internal string? PropertyLabel(IMemberNode member) => NumericPresentation?.Label(member);
    internal Stride.Core.ExpandRule PropertyExpansion(IMemberNode member)
        => NumericPresentation?.Expansion(member) ?? Stride.Core.ExpandRule.Auto;
    internal IMemberNode? InlineProperty(IMemberNode member) => NumericPresentation?.InlineMember(member);
    internal IReadOnlyList<AbstractNodeEntry> PropertyEntries(IMemberNode member)
        => NumericPresentation?.AbstractEntries(member)?.OrderBy(entry => entry.Order).ToArray() ?? [];
    internal EditorPresenterTarget? AcquireExtensionCommandTarget(IMemberNode member)
        => NumericPresentation?.AcquireCommandTarget(member);
    internal EditorPresenterTarget AcquirePresenterTarget(Stride.Core.Presentation.Quantum.Presenters.INodePresenter presenter)
        => NumericPresentation?.AcquireTarget(presenter) ?? throw new InvalidOperationException("The procedural presenter is no longer available.");
    internal AssetPropertyField PropertyField(IMemberNode member, string? path = null)
    {
        var display = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(member.MemberDescriptor.MemberInfo);
        return new(member, display?.Name ?? Stride.Core.Presentation.Core.Utils.SplitCamelCase(member.Name), path);
    }
    internal bool CanSetObjectPresence(AssetPropertyField field, bool create)
        => IsPropertyFieldCurrent(field) && CanEditAssetSettingNode(field.Node) && !NumericReadOnly(field.Node)
            && (create ? field.Node.Retrieve() == null && ObjectFactoryRegistry.CanCreateInstance(field.Type)
                : field.Node.Retrieve() != null && field.AllowsNull);
    internal void SetObjectPresence(AssetPropertyField field, object? expected, bool create)
    {
        if (!CanSetObjectPresence(field, create) || !ReferenceEquals(expected, field.Node.Retrieve()))
            throw new InvalidOperationException("The procedural object changed.");
        EndEdit();
        var value = create ? ObjectFactoryRegistry.NewInstance(field.Type) : null;
        if (create && value == null) throw new InvalidOperationException("The native object factory returned null.");
        if (value != null) AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
        ApplyInheritedAssetMutation(() => field.Node.Update(value), "Edit " + field.Label);
    }
    internal void ReplacePropertyEntry(IMemberNode member, AbstractNodeEntry entry)
    {
        if (!CanEditAssetSettingNode(member) || NumericReadOnly(member) || !NativeAbstractEntries.Contains(PropertyEntries(member), entry))
            throw new InvalidOperationException("The native procedural factory entry is no longer available.");
        if (entry.IsMatchingValue(member.Retrieve())) return;
        var value = entry.GenerateValue(member.Retrieve());
        var field = PropertyField(member);
        if (value == null && !field.AllowsNull) throw new InvalidOperationException("The procedural property cannot be null.");
        if (value != null) AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
        ApplyInheritedAssetMutation(() => member.Update(value), "Edit " + field.Label);
    }
}
