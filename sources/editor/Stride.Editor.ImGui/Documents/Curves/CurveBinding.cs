using Stride.Animations;
using Stride.Core.Mathematics;
using Stride.Core.Quantum;
using Stride.Core;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed class CurveBinding : IDisposable
{
    private readonly IMemberNode member;
    private readonly object root;
    private readonly IDisposable lease;
    private readonly List<Func<bool>> links = [];
    internal CurveAuthoringContext Context { get; }
    internal string Title { get; }
    internal object Identity => member;
    internal IReadOnlyList<ICurveTrack> Tracks { get; }
    internal CurveHierarchyNode Hierarchy { get; }
    internal bool IsCurrent => Context.IsCurrent && ReferenceEquals(member.Retrieve(), root) && links.All(check => check());
    internal static bool IsSupported(object? value) => value is IComputeCurve<float> or IComputeCurve<Vector2>
        or IComputeCurve<Vector3> or IComputeCurve<Vector4> or IComputeCurve<Quaternion> or IComputeCurve<Color4>;
    internal static CurveBinding Create(CurveAuthoringContext context, IMemberNode member, string title) => new(context, member, title);
    private CurveBinding(CurveAuthoringContext context, IMemberNode member, string title)
    {
        Context = context; this.member = member; root = member.Retrieve()!; Title = title;
        var tracks = new List<ICurveTrack>();
        switch (root)
        {
            case IComputeCurve<float> curve: Hierarchy = Add(curve, title, ["Value"], tracks); break;
            case IComputeCurve<Vector2> curve: Hierarchy = Add(curve, title, ["X", "Y"], tracks); break;
            case IComputeCurve<Vector3> curve: Hierarchy = Add(curve, title, ["X", "Y", "Z"], tracks); break;
            case IComputeCurve<Vector4> curve: Hierarchy = Add(curve, title, ["X", "Y", "Z", "W"], tracks); break;
            case IComputeCurve<Quaternion> curve: Hierarchy = Add(curve, title, ["X", "Y", "Z", "W"], tracks); break;
            case IComputeCurve<Color4> curve: Hierarchy = Add(curve, title, ["R", "G", "B", "A"], tracks); break;
            default: throw new NotSupportedException("This compute curve type is unsupported.");
        }
        Tracks = tracks; lease = context.Owner.AcquireView();
    }
    private CurveHierarchyNode Add<T>(IComputeCurve<T> curve, string name, string[] channels, List<ICurveTrack> tracks, HashSet<object>? visited = null) where T : struct
    {
        visited ??= new(ReferenceEqualityComparer.Instance);
        if (!visited.Add(curve)) throw new InvalidOperationException("The curve hierarchy contains a cycle.");
        var components = new List<ICurveTrack>();
        if ((object)curve is IComputeCurve<Quaternion> rotation && curve is not ComputeAnimationCurve<T>)
        {
            var display = new RotationCurveDisplay(rotation);
            for (var channel = 0; channel < 4; channel++) components.Add(new RotationCurveTrack(display, name, channel));
        }
        else for (var channel = 0; channel < channels.Length; channel++)
            components.Add(new CurveTrack<T>(this, curve, channels.Length == 1 ? name : channels[channel], channel));
        ICurveTrack parent = components.Count == 1 || components[0] is RotationCurveTrack
            ? components[0] : new CompoundCurveTrack(name, components);
        tracks.Add(parent);
        foreach (var component in components) if (!ReferenceEquals(component, parent)) tracks.Add(component);
        var children = new List<CurveHierarchyNode>();
        if (components.Count > 1 && curve is ComputeAnimationCurve<T>)
            foreach (var component in components) children.Add(new(component.Name, component, []));
        if (curve is ComputeBinaryCurve<T> binary)
        {
            var left = binary.LeftChild; var right = binary.RightChild;
            links.Add(() => ReferenceEquals(binary.LeftChild, left) && ReferenceEquals(binary.RightChild, right));
            if (left != null) children.Add(Add(left, ChildName<T>(nameof(binary.LeftChild), "Left"), channels, tracks, visited));
            if (right != null) children.Add(Add(right, ChildName<T>(nameof(binary.RightChild), "Right"), channels, tracks, visited));
        }
        visited.Remove(curve);
        return new(name, parent, children);
    }
    private static string ChildName<T>(string memberName, string fallback) where T : struct
        => TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(typeof(ComputeBinaryCurve<T>).GetProperty(memberName))?.Name ?? fallback;
    public void Dispose() => lease.Dispose();
}

internal sealed record CurveHierarchyNode(string Name, ICurveTrack Track, IReadOnlyList<CurveHierarchyNode> Children);

internal sealed record CurvePoint(object Value, float Key, float Component, AnimationKeyTangentType Tangent);
internal interface ICurveTrack
{
    string Name { get; }
    object CurveIdentity { get; }
    int Channel { get; }
    IEnumerable<float> SamplingKeys { get; }
    bool Editable { get; }
    CurvePointNumericTarget? CaptureNumericTarget(object point, bool key) => null;
    IReadOnlyList<CurvePoint> ReadPoints();
    void PrepareSampling();
    float Sample(float key);
    void Add(float key, float value);
    void Edit(IReadOnlyList<(object Point, float Key, float Value)> edits);
    void SetTangent(IEnumerable<object> points, AnimationKeyTangentType tangent);
    void Delete(IEnumerable<object> points);
    string Copy(IEnumerable<object> points);
    bool CanPaste(string text);
    IReadOnlyList<object> Paste(string text);
}
