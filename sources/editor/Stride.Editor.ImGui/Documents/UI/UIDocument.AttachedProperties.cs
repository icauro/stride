using System.Reflection;
using System.Runtime.CompilerServices;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Reflection;
using Stride.UI;

namespace Stride.Editor.ImGui;

internal sealed record UIAttachedProperty(PropertyKey Key, string Label, string Category, int Order);

internal sealed partial class UIDocument
{
    private static readonly ConditionalWeakTable<Type, AttachedKeyFields> attachedKeyFields = new();
    private sealed class AttachedKeyFields(Type type)
    {
        internal readonly FieldInfo[] Fields = Enumerate(type).ToArray();
        private static IEnumerable<FieldInfo> Enumerate(Type? type)
        {
            if (type == null) yield break;
            foreach (var field in Enumerate(type.BaseType)) yield return field;
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                if (typeof(PropertyKey).IsAssignableFrom(field.FieldType)) yield return field;
        }
    }
    internal IReadOnlyList<UIAttachedProperty> AttachedProperties(Guid id)
    {
        var parent = Find(id)?.VisualParent;
        if (parent == null) return [];
        var result = new List<UIAttachedProperty>();
        var seen = new HashSet<PropertyKey>();
        var order = 10000;
        foreach (var field in attachedKeyFields.GetValue(parent.GetType(), type => new(type)).Fields)
        {
            if (field.GetValue(null) is not PropertyKey key || !seen.Add(key)
                || !key.Metadatas.OfType<DependencyPropertyKeyMetadata>().Any(metadata => metadata.Flags.HasFlag(DependencyPropertyFlags.Attached))) continue;
            // The original updater reads display metadata from the actual key's declaring field.
            var declared = key.OwnerType.GetField(key.Name, BindingFlags.Public | BindingFlags.Static) ?? field;
            var display = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(declared);
            var name = string.IsNullOrEmpty(display?.Name) ? key.Name.Replace("PropertyKey", "") : display.Name;
            result.Add(new(key, parent.GetType().Name + "." + Stride.Core.Presentation.Core.Utils.SplitCamelCase(name), display?.Category ?? "", display?.Order ?? order++));
        }
        return result.OrderBy(property => property.Order).ToArray();
    }
    internal IReadOnlyList<UIAttachedProperty> CommonAttachedProperties(IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0) return [];
        var choices = ids.Select(AttachedProperties).ToArray();
        return choices[0].Where(property => choices.Skip(1).All(other => other.Any(candidate => ReferenceEquals(candidate.Key, property.Key)))).ToArray();
    }
    internal object? ReadDependencyProperty(Guid id, PropertyKey key)
    {
        if (!HasDependencyProperty(id, key)) throw new InvalidOperationException("The attached property is not declared by this element's parent.");
        return DependencyPropertyNode(id).Retrieve(new Stride.Core.Quantum.NodeIndex(key));
    }
    internal void TransformDependencyPropertyValues(IReadOnlyList<Guid> ids, PropertyKey key, Func<object?, object?> transform)
    {
        EndEdit();
        if (ids.Count == 0 || ids.Any(id => !CanChangeDependencyProperty(id, key))) throw new InvalidOperationException("The attached UI property cannot be edited.");
        var prepared = ids.Distinct().Select(id =>
        {
            var previous = ReadDependencyProperty(id, key); var proposed = transform(previous); var node = DependencyPropertyNode(id);
            AssertUIItemAxesEditable(node, new Stride.Core.Quantum.NodeIndex(key), previous, proposed);
            return (Node: node, Value: NativeNumericAxes.Normalize(
                PropertyValueValidation.Normalize(proposed, UIAttachedNumericRange(id, key)), previous, UIAttachedAxisNumericRanges(id, key)));
        }).ToArray();
        if (prepared.Any(item => item.Value == null ? key.PropertyType.IsValueType && Nullable.GetUnderlyingType(key.PropertyType) == null : !key.PropertyType.IsInstanceOfType(item.Value)))
            throw new ArgumentException("The attached property value has an incompatible type.");
        foreach (var item in prepared)
            if (item.Value != null && !ReferenceEquals(item.Value, item.Node.Retrieve(new Stride.Core.Quantum.NodeIndex(key))))
                AssetCollectionItemIdHelper.GenerateMissingItemIds(item.Value);
        var index = new Stride.Core.Quantum.NodeIndex(key);
        Mutate(() =>
        {
            foreach (var item in prepared)
                if (item.Node.Indices.Contains(index)) item.Node.Update(item.Value, index); else item.Node.Add(item.Value, index);
        }, "Change " + key.Name);
    }
}
