using Stride.Animations;
using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel.CopyPasteProcessors;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Serializers;
using Stride.Core.Mathematics;
using Stride.Core.Quantum;
using Stride.Core.Yaml;

namespace Stride.Editor.ImGui;

internal sealed class CurveTrack<T>(CurveBinding binding, IComputeCurve<T> curve, string name, int channel) : ICurveTrack where T : struct
{
    private ComputeAnimationCurve<T>? Animation => curve as ComputeAnimationCurve<T>;
    private IAssetObjectNode Collection => (IAssetObjectNode)binding.Context.Graph.Container.NodeContainer.GetOrCreateNode(Animation!)[nameof(ComputeAnimationCurve<T>.KeyFrames)].Target!;
    private readonly List<CurvePoint> points = [];
    private CopyPasteService? clipboard;
    public string Name => name;
    public object CurveIdentity => curve;
    public int Channel => channel;
    public IEnumerable<float> SamplingKeys => CurveSamplingKeys.Enumerate(curve);
    public bool Editable => binding.IsCurrent && Animation != null && binding.Context.CanEdit(Collection);
    public IReadOnlyList<CurvePoint> ReadPoints()
    {
        points.Clear();
        if (Animation is { } animation)
            foreach (var point in animation.KeyFrames) points.Add(new(point, point.Key, Get(point.Value, channel), point.TangentType));
        return points;
    }
    public void PrepareSampling() => curve.UpdateChanges();
    public float Sample(float key) => Get(curve.Evaluate(key), channel);
    private AnimationKeyFrame<T>[] Resolve(IEnumerable<object> selected)
    {
        if (!Editable) throw new InvalidOperationException("This curve cannot be edited.");
        var result = selected.Distinct(ReferenceEqualityComparer.Instance).Select(value => value as AnimationKeyFrame<T>
            ?? throw new InvalidOperationException("The selected point is incompatible.")).ToArray();
        foreach (var point in result)
        {
            var index = Animation!.KeyFrames.IndexOf(point);
            if (index < 0 || !binding.Context.CanEditItem(Collection, new(index))) throw new InvalidOperationException("The selected curve point is sealed or no longer exists.");
        }
        return result;
    }
    private IObjectNode PointNode(AnimationKeyFrame<T> point) => binding.Context.Graph.Container.NodeContainer.GetOrCreateNode(point);
    public CurvePointNumericTarget? CaptureNumericTarget(object value, bool key)
    {
        if (value is not AnimationKeyFrame<T> point || Animation == null || !binding.IsCurrent) return null;
        var index = Animation.KeyFrames.IndexOf(point);
        if (index < 0) return null;
        var collection = Collection; var id = collection.IndexToId(new NodeIndex(index));
        var node = PointNode(point); var member = node[key ? nameof(point.Key) : nameof(point.Value)];
        bool Current() => binding.IsCurrent && ReferenceEquals(Collection, collection) && collection.HasId(id)
            && ReferenceEquals(collection.Retrieve(collection.IdToIndex(id)), point)
            && binding.Context.CanEditItem(collection, collection.IdToIndex(id)) && binding.Context.CanEdit(member);
        object?[] Read()
        {
            var values = new List<object?> { key ? point.Key : Get(point.Value, channel), point.Key, point.Value, point.TangentType,
                binding.Identity, binding.Context.Owner.Generation, (binding.Context.Owner.Document as IAssetTagDocument)?.EditRevision,
                collection, id, collection.IdToIndex(id), node, member, channel, curve };
            values.Add((collection.Indices ?? []).Count());
            return values.ToArray();
        }
        return new(binding.Context.Owner, point, Read, Current, number =>
        {
            if (!Current() || !float.IsFinite(number)) throw new InvalidOperationException("The curve point changed or is not editable.");
            Edit([(point, key ? number : point.Key, key ? Get(point.Value, channel) : number)]);
        });
    }
    private void CheckMember(AnimationKeyFrame<T> point, string member)
    {
        if (!binding.Context.CanEdit(PointNode(point)[member])) throw new InvalidOperationException("The curve point property is sealed.");
    }
    public void Add(float key, float value)
    {
        if (!Editable || !float.IsFinite(key) || !float.IsFinite(value)) throw new InvalidOperationException("Invalid curve point.");
        curve.UpdateChanges();
        var point = new AnimationKeyFrame<T> { Key = key, Value = Set(curve.Evaluate(key), channel, value) };
        binding.Context.Mutate(() => Collection.Add(point, new NodeIndex(InsertIndex(key))), "Add control point");
    }
    private int InsertIndex(float key)
    {
        for (var index = 0; index < Animation!.KeyFrames.Count; index++) if (Animation.KeyFrames[index].Key > key) return index;
        return Animation.KeyFrames.Count;
    }
    public void Edit(IReadOnlyList<(object Point, float Key, float Value)> edits)
    {
        var selected = Resolve(edits.Select(edit => edit.Point));
        if (selected.Length != edits.Count || edits.Any(edit => !float.IsFinite(edit.Key) || !float.IsFinite(edit.Value))) throw new InvalidOperationException("Invalid curve point changes.");
        foreach (var edit in edits)
        {
            var point = (AnimationKeyFrame<T>)edit.Point;
            if (point.Key != edit.Key) CheckMember(point, nameof(point.Key));
            if (Get(point.Value, channel) != edit.Value) CheckMember(point, nameof(point.Value));
        }
        if (edits.All(edit => ((AnimationKeyFrame<T>)edit.Point).Key == edit.Key && Get(((AnimationKeyFrame<T>)edit.Point).Value, channel) == edit.Value)) return;
        binding.Context.Mutate(() =>
        {
            foreach (var edit in edits)
            {
                var point = (AnimationKeyFrame<T>)edit.Point;
                PointNode(point)[nameof(point.Key)].Update(edit.Key);
                PointNode(point)[nameof(point.Value)].Update(Set(point.Value, channel, edit.Value));
            }
            // WPF reorders a point that crosses its neighbours by native remove/add.
            foreach (var point in selected.OrderBy(point => point.Key))
            {
                var index = Animation!.KeyFrames.IndexOf(point);
                if (index > 0 && Animation.KeyFrames[index - 1].Key > point.Key
                    || index + 1 < Animation.KeyFrames.Count && Animation.KeyFrames[index + 1].Key < point.Key)
                { Collection.Remove(point, new NodeIndex(index)); Collection.Add(point, new NodeIndex(InsertIndex(point.Key))); }
            }
        }, "Move control points");
    }
    public void SetTangent(IEnumerable<object> selected, AnimationKeyTangentType tangent)
    {
        if (!Enum.IsDefined(tangent)) throw new ArgumentOutOfRangeException(nameof(tangent));
        var values = Resolve(selected);
        foreach (var point in values) CheckMember(point, nameof(point.TangentType));
        if (values.All(point => point.TangentType == tangent)) return;
        binding.Context.Mutate(() => { foreach (var point in values) PointNode(point)[nameof(point.TangentType)].Update(tangent); }, "Edit tangent");
    }
    public void Delete(IEnumerable<object> selected)
    {
        var values = Resolve(selected);
        if (values.Length == 0) return;
        binding.Context.Mutate(() =>
        {
            foreach (var point in values.OrderByDescending(point => Animation!.KeyFrames.IndexOf(point))) Collection.Remove(point, new NodeIndex(Animation!.KeyFrames.IndexOf(point)));
        }, "Delete control points");
    }
    private CopyPasteService Clipboard
    {
        get
        {
            if (clipboard != null) return clipboard;
            var settings = AssetYamlSerializer.Default.GetSerializerSettings();
            settings.RegisterTagMapping("!CopyPasteData", typeof(CopyPasteData)); settings.RegisterTagMapping("!CopyPasteItem", typeof(CopyPasteItem));
            clipboard = new(binding.Context.Graph.Container); clipboard.RegisterProcessor(new AssetPropertyPasteProcessor());
            return clipboard;
        }
    }
    public string Copy(IEnumerable<object> selected)
    {
        if (!binding.IsCurrent || Animation == null) return "";
        var values = selected.OfType<AnimationKeyFrame<T>>().Where(Animation.KeyFrames.Contains).OrderBy(point => point.Key).ToList();
        if (values.Count == 0) return "";
        return Clipboard.CopyFromAsset(binding.Context.Graph, binding.Context.Asset.Id, values, false);
    }
    public bool CanPaste(string text) => Editable && !string.IsNullOrWhiteSpace(text) && text.Length <= 16 * 1024 * 1024
        && Clipboard.CanPaste(text, typeof(List<AnimationKeyFrame<T>>), typeof(List<AnimationKeyFrame<T>>), typeof(List<AnimationKeyFrame<T>>));
    public IReadOnlyList<object> Paste(string text)
    {
        if (!CanPaste(text)) throw new InvalidOperationException("The clipboard does not contain compatible control points.");
        var result = Clipboard.DeserializeCopiedData(text, binding.Context.Asset, typeof(List<AnimationKeyFrame<T>>));
        if (result.Items.Count != 1 || result.Items[0] is not { Processor: AssetPropertyPasteProcessor, Data: List<AnimationKeyFrame<T>> values }
            || values.Count > 100000 || values.Any(point => point == null || !float.IsFinite(point.Key) || !Finite(point.Value) || !Enum.IsDefined(point.TangentType)))
            throw new InvalidOperationException("Invalid copied control points.");
        binding.Context.Mutate(() => { foreach (var point in values.OrderBy(point => point.Key)) Collection.Add(point, new NodeIndex(InsertIndex(point.Key))); }, "Paste control points");
        return values.Cast<object>().ToArray();
    }
    private static bool Finite(T value)
    {
        var count = typeof(T) == typeof(float) ? 1 : typeof(T) == typeof(Vector2) ? 2 : typeof(T) == typeof(Vector3) ? 3 : 4;
        for (var index = 0; index < count; index++) if (!float.IsFinite(Get(value, index))) return false;
        return true;
    }
    private static float Get(T value, int component) => (object)value switch
    {
        float number => number,
        Vector2 vector => component == 0 ? vector.X : vector.Y,
        Vector3 vector => component == 0 ? vector.X : component == 1 ? vector.Y : vector.Z,
        Vector4 vector => component == 0 ? vector.X : component == 1 ? vector.Y : component == 2 ? vector.Z : vector.W,
        Quaternion rotation => component == 0 ? rotation.X : component == 1 ? rotation.Y : component == 2 ? rotation.Z : rotation.W,
        Color4 color => component == 0 ? color.R : component == 1 ? color.G : component == 2 ? color.B : color.A,
        _ => throw new NotSupportedException()
    };
    private static T Set(T original, int component, float value)
    {
        object boxed = original;
        switch (boxed)
        {
            case float: boxed = value; break;
            case Vector2 vector: if (component == 0) vector.X = value; else vector.Y = value; boxed = vector; break;
            case Vector3 vector: if (component == 0) vector.X = value; else if (component == 1) vector.Y = value; else vector.Z = value; boxed = vector; break;
            case Vector4 vector: if (component == 0) vector.X = value; else if (component == 1) vector.Y = value; else if (component == 2) vector.Z = value; else vector.W = value; boxed = vector; break;
            case Quaternion rotation: if (component == 0) rotation.X = value; else if (component == 1) rotation.Y = value; else if (component == 2) rotation.Z = value; else rotation.W = value; boxed = rotation; break;
            case Color4 color: if (component == 0) color.R = value; else if (component == 1) color.G = value; else if (component == 2) color.B = value; else color.A = value; boxed = color; break;
            default: throw new NotSupportedException();
        }
        return (T)boxed;
    }
}
