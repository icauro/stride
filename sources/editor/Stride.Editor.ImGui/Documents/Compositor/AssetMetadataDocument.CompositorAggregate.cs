using Stride.Core.Quantum;
using Stride.Core.Assets.Quantum;
using Stride.Rendering.Images;

namespace Stride.Editor.ImGui;

internal sealed record CompositorAggregateProperty(string Key, string Label, IReadOnlyList<CompositorProperty> Fields)
{
    public IReadOnlyList<CompositorAggregateProperty> Children { get; init; } = [];
    public IReadOnlyList<CompositorAggregateProperty> Items { get; init; } = [];
    public IReadOnlyList<CompositorAggregateProperty> Details { get; init; } = [];
    public CompositorAggregateProperty? InlineMember { get; init; }
    public CompositorAggregateProperty? EnabledMember { get; init; }
    public CompositorProperty First => Fields[0];
    public object? Value => First.Accessor.RetrieveValue();
    public bool IsMixed
    {
        get
        {
            var value = Value;
            for (var index = 1; index < Fields.Count; index++)
                if (!ContentAssetReference.AreEqual(value, Fields[index].Accessor.RetrieveValue())) return true;
            return false;
        }
    }
}

internal partial class AssetMetadataDocument
{
    public IReadOnlyList<CompositorAggregateProperty> GetCompositorCommonProperties(IReadOnlyList<(IObjectNode Node, bool Entry)> roots)
    {
        return roots.Count < 2 ? [] : MergeCompositorProperties(roots.Select(root => GetCompositorProperties(root.Node, root.Entry)).ToArray(), "");
    }
    private static IReadOnlyList<CompositorAggregateProperty> MergeCompositorProperties(IReadOnlyList<IReadOnlyList<CompositorProperty>> sources, string prefix)
    {
        var result = new List<CompositorAggregateProperty>();
        foreach (var first in sources[0])
        {
            var memberName = first.Accessor.Node is IMemberNode firstMember ? firstMember.Name : null;
            var fields = new List<CompositorProperty> { first };
            for (var index = 1; index < sources.Count; index++)
            {
                var next = sources[index].FirstOrDefault(field => first.NativePresenter is { } native ? field.NativePresenter?.CombineKey == native.CombineKey
                    : memberName != null ? field.Accessor.Node is IMemberNode member && member.Name == memberName
                    : field.Accessor.Index == first.Accessor.Index);
                if (next == null || (first.NativePresenter != null
                    ? next.NativePresenter == null || !Stride.Core.Presentation.Quantum.Presenters.NodePresenterCombination.CanConstruct([first.NativePresenter, next.NativePresenter])
                    : next.DeclaredType != first.DeclaredType || first.Accessor.Node is IMemberNode original
                        && (next.Accessor.Node is not IMemberNode member || !Equals(member.MemberDescriptor.MemberInfo, original.MemberDescriptor.MemberInfo)))) break;
                fields.Add(next);
            }
            if (fields.Count != sources.Count) continue;
            var key = prefix + "/" + (first.NativePresenter?.CombineKey ?? memberName ?? first.Accessor.Index.Value?.ToString() ?? "");
            var children = MergeCompositorProperties(fields.Select(field => field.Children).ToArray(), key);
            var inline = children.FirstOrDefault(child => ReferenceEquals(child.First, first.InlineMember));
            var enabled = children.FirstOrDefault(child => ReferenceEquals(child.First, first.EnabledMember));
            result.Add(new(key, first.Label, fields)
            {
                Children = children, InlineMember = inline, EnabledMember = enabled,
                Details = children.Where(child => !ReferenceEquals(child, inline) && !ReferenceEquals(child, enabled)).ToArray(),
                Items = MergeCompositorProperties(fields.Select(field => field.Items).ToArray(), key + "/items"),
            });
        }
        return result;
    }
    internal IReadOnlyList<CompositorAggregateProperty> GetCompositorNativeChildProperties(IReadOnlyList<Stride.Core.Presentation.Quantum.Presenters.INodePresenter> parents)
    {
        var result = new List<CompositorAggregateProperty>();
        Visit(parents);
        return result;
        void Visit(IReadOnlyList<Stride.Core.Presentation.Quantum.Presenters.INodePresenter> owners)
        {
            foreach (var group in Stride.Core.Presentation.Quantum.Presenters.NodePresenterCombination.CombineChildren(owners))
            {
                if (!Stride.Core.Presentation.Quantum.Presenters.NodePresenterCombination.CanConstruct(group)) continue;
                if (group.All(NativePresenterCategories.IsCategory)) { Visit(group); continue; }
                if (group.Any(presenter => presenter is Stride.Core.Presentation.Quantum.Presenters.VirtualNodePresenter)) continue;
                result.AddRange(MergeCompositorProperties(group.Select(presenter => (IReadOnlyList<CompositorProperty>)new[] { GetCompositorNativeChildProperty(presenter) }).ToArray(), "native"));
            }
        }
    }
    public bool CanEditCompositorAggregate(CompositorAggregateProperty property)
    {
        foreach (var field in property.Fields) if (!CanEditCompositorProperty(field)) return false;
        return true;
    }
    public bool CanResetCompositorAggregate(CompositorAggregateProperty property)
    {
        foreach (var field in property.Fields)
            if (!compositorPropertyMembers.Contains(field) || (field.Accessor.IsItem
                ? !CanEditCompositorProperty(field) || field.Accessor.Node is not IAssetObjectNode collection || !CanResetAssetSettingItem(collection, field.Accessor.Index)
                : !CanResetAssetSettingNode(field.Accessor.Node))) return false;
        return property.Fields.Count > 0;
    }
    public void ResetCompositorAggregate(CompositorAggregateProperty property)
    {
        CommitEdits();
        if (!CanResetCompositorAggregate(property)) throw new InvalidOperationException("The selected compositor properties have no editable common base value.");
        var fields = property.Fields.DistinctBy(field => field.Accessor).ToArray();
        ApplyInheritedAssetMutation(() =>
        {
            foreach (var field in fields)
                if (field.Accessor.IsItem) ((IAssetObjectNode)field.Accessor.Node).ResetOverrideRecursively(field.Accessor.Index);
                else ((IAssetNode)field.Accessor.Node).ResetOverrideRecursively();
        }, "Reset " + property.First.Label);
    }
    public void EditCompositorAggregate(CompositorAggregateProperty property, Func<CompositorProperty, object?> createValue)
    {
        CommitEdits();
        if (!CanEditCompositorAggregate(property)) throw new InvalidOperationException("The selected compositor properties cannot be edited together.");
        var values = property.Fields.Select(field => NormalizeCompositorProperty(field, createValue(field))).ToArray();
        var qualityUpdates = new Dictionary<IMemberNode, int>();
        var seen = new Dictionary<(IGraphNode, NodeIndex), object?>();
        for (var index = 0; index < property.Fields.Count; index++)
        {
            var field = property.Fields[index]; var key = (field.Accessor.Node, field.Accessor.Index);
            if (seen.TryGetValue(key, out var previous) && !ContentAssetReference.AreEqual(previous, values[index]))
                throw new InvalidOperationException("Aliases of the same compositor property cannot receive different values.");
            seen[key] = values[index];
            if (field.Accessor.Node is not IMemberNode dither || dither.Name != nameof(FXAAEffect.Dither) || dither.Parent.Retrieve() is not FXAAEffect
                || values[index] is not FXAAEffect.DitherType mode) continue;
            var (minimum, maximum) = FXAAEffect.GetQualityRange(mode); var quality = dither.Parent[nameof(FXAAEffect.Quality)];
            var value = Math.Clamp((int)quality.Retrieve(), minimum, maximum);
            if (Equals(value, quality.Retrieve())) continue;
            if (!CanEditAssetSettingNode(quality) || !CanAssignAuthoringNode(quality)) throw new InvalidOperationException("The FXAA quality required by this dither mode is read-only.");
            qualityUpdates[quality] = value;
        }
        if (seen.All(pair => ContentAssetReference.AreEqual(new NodeAccessor(pair.Key.Item1, pair.Key.Item2).RetrieveValue(), pair.Value)) && qualityUpdates.Count == 0) return;
        ApplyInheritedAssetMutation(() =>
        {
            foreach (var (key, value) in seen) new NodeAccessor(key.Item1, key.Item2).UpdateValue(value);
            foreach (var (node, value) in qualityUpdates) node.Update(value);
        }, "Edit " + property.First.Label);
    }
}
