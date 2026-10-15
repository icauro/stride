using Stride.Core.Quantum;
using Stride.Core.Assets;
using Stride.Core.Reflection;
using System.Reflection;
using Stride.Core;
using Stride.Core.Assets.Quantum;
using Stride.UI;

namespace Stride.Editor.ImGui;

internal sealed partial class UIDocument
{
    internal string CopyProperty(Guid id, string path)
        => CopyAssetProperty(Properties(id).Single(field => field.Name == path).Node);
    internal bool CanPasteProperties(IReadOnlyList<Guid> ids, string path, string text)
    {
        var fields = ids.Select(id => Properties(id).FirstOrDefault(field => field.Name == path)).ToArray();
        return fields.Length != 0 && fields.All(field => field != null && CanEdit(field))
            && CanPasteAssetProperties(fields.Select(field => field!.Node).ToArray(), text);
    }
    internal void PasteProperties(IReadOnlyList<Guid> ids, string path, string text, bool replace)
    {
        if (!CanPasteProperties(ids, path, text)) throw new InvalidOperationException("The selected UI properties cannot be pasted.");
        var fields = ids.Select(id => Properties(id).Single(field => field.Name == path)).ToArray();
        applyingUIEdit = true;
        try
        {
            PasteAssetProperties(fields.Select(field => field.Node).ToArray(), text, replace,
                (member, value) => NormalizeUIProperty(fields.Single(field => ReferenceEquals(field.Node, member)), value)!);
        }
        finally { applyingUIEdit = false; }
    }
    internal bool HasDependencyProperty(Guid id, PropertyKey key)
    {
        var type = Find(id)?.VisualParent?.GetType();
        if (!key.Metadatas.OfType<DependencyPropertyKeyMetadata>().Any(metadata => metadata.Flags.HasFlag(DependencyPropertyFlags.Attached))) return false;
        while (type != null)
        {
            if (type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Any(field => typeof(PropertyKey).IsAssignableFrom(field.FieldType) && ReferenceEquals(field.GetValue(null), key))) return true;
            type = type.BaseType;
        }
        return false;
    }
    private IAssetObjectNode DependencyPropertyNode(Guid id)
        => (IAssetObjectNode)ObjectNode(Require(id))[nameof(UIElement.DependencyProperties)].Target!;
    internal string CopyDependencyProperty(Guid id, PropertyKey key)
    {
        if (!HasDependencyProperty(id, key)) throw new InvalidOperationException("The attached property is not declared by this element's parent.");
        return CopyAssetPropertyAccessor(new(DependencyPropertyNode(id), new NodeIndex(key)));
    }
    internal bool CanPasteDependencyProperty(IReadOnlyList<Guid> ids, PropertyKey key, string text)
    {
        return ids.Count != 0 && ids.All(id => CanChangeDependencyProperty(id, key)) && CanPrepareAssetPropertyValue(text, key.PropertyType);
    }
    internal bool CanChangeDependencyProperty(Guid id, PropertyKey key)
        => HasDependencyProperty(id, key) && !key.Metadatas.OfType<DependencyPropertyKeyMetadata>()
            .Any(metadata => metadata.Flags.HasFlag(DependencyPropertyFlags.ReadOnly))
        && CanEditAssetSettingNode(DependencyPropertyNode(id)) && !AuthoringGraph.IsInheritedNodeSealed(DependencyPropertyNode(id), new NodeIndex(key));
    internal void TransformDependencyProperties<T>(IReadOnlyList<Guid> ids, PropertyKey<T> key, Func<T, T> transform)
    {
        EndEdit();
        if (ids.Count == 0 || ids.Any(id => !CanChangeDependencyProperty(id, key))) throw new InvalidOperationException("The attached UI property cannot be edited.");
        var prepared = ids.Distinct().Select(id => (Node: DependencyPropertyNode(id), Value: transform(Require(id).DependencyProperties.Get(key)))).ToArray();
        var index = new NodeIndex(key);
        Mutate(() =>
        {
            foreach (var item in prepared)
                if (item.Node.Indices.Contains(index)) item.Node.Update(item.Value, index); else item.Node.Add(item.Value, index);
        }, "Change " + key.Name);
    }
    internal bool CanResetDependencyProperties(IReadOnlyList<Guid> ids, PropertyKey key)
        => ids.Count != 0 && ids.All(id => CanChangeDependencyProperty(id, key))
            && ids.Any(id => CanResetDependencyProperty(id, key));
    internal bool CanResetDependencyProperty(Guid id, PropertyKey key)
    {
        if (!CanChangeDependencyProperty(id, key)) return false;
        var node = DependencyPropertyNode(id); var index = new NodeIndex(key);
        // A locally added inherited key is reset by removal even when the base has no matching item ID.
        return node.BaseNode != null && node.Indices.Contains(index) && node.TryIndexToId(index, out var item) && item != ItemId.Empty;
    }
    internal void ResetDependencyProperties(IReadOnlyList<Guid> ids, PropertyKey key)
    {
        EndEdit();
        if (!CanResetDependencyProperties(ids, key)) throw new InvalidOperationException("The attached UI property cannot be reset.");
        var index = new NodeIndex(key);
        Mutate(() =>
        {
            foreach (var id in ids.Distinct())
            {
                var node = DependencyPropertyNode(id);
                if (CanResetDependencyProperty(id, key)) node.ResetOverrideRecursively(index);
            }
        }, "Reset to base value");
    }
    internal void PasteDependencyProperty(IReadOnlyList<Guid> ids, PropertyKey key, string text, bool replace = false)
    {
        EndEdit();
        if (!CanPasteDependencyProperty(ids, key, text)) throw new InvalidOperationException("The clipboard cannot be pasted into this attached UI property.");
        var prepared = ids.Distinct().Select(id => (Node: DependencyPropertyNode(id), Value: PrepareAssetPropertyValue(text, key.PropertyType))).ToArray();
        if (key.PropertyType.IsValueType && Nullable.GetUnderlyingType(key.PropertyType) == null && prepared.Any(item => item.Value == null))
            throw new InvalidOperationException("The clipboard did not produce an attached property value.");
        var index = new NodeIndex(key);
        Mutate(() =>
        {
            foreach (var item in prepared)
                if (item.Node.Indices.Contains(index)) item.Node.Update(item.Value, index); else item.Node.Add(item.Value, index);
        }, replace ? "Replace property" : "Paste property");
    }
}
