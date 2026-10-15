using Stride.Animations;

namespace Stride.Editor.ImGui;

// A presentation of the real engine curve's channels; it owns no authored values.
internal sealed class CompoundCurveTrack(string name, IReadOnlyList<ICurveTrack> channels) : ICurveTrack
{
    internal IReadOnlyList<ICurveTrack> Channels => channels;
    public string Name => name;
    public object CurveIdentity => channels[0].CurveIdentity;
    public int Channel => -1;
    public IEnumerable<float> SamplingKeys => channels[0].SamplingKeys;
    public bool Editable => false;
    public IReadOnlyList<CurvePoint> ReadPoints() => [];
    public void PrepareSampling() => channels[0].PrepareSampling();
    public float Sample(float key) => channels[0].Sample(key);
    private static InvalidOperationException ReadOnly() => new("The composite curve view is read-only.");
    public void Add(float key, float value) => throw ReadOnly();
    public void Edit(IReadOnlyList<(object Point, float Key, float Value)> edits) => throw ReadOnly();
    public void SetTangent(IEnumerable<object> points, AnimationKeyTangentType tangent) => throw ReadOnly();
    public void Delete(IEnumerable<object> points) => throw ReadOnly();
    public string Copy(IEnumerable<object> points) => "";
    public bool CanPaste(string text) => false;
    public IReadOnlyList<object> Paste(string text) => throw ReadOnly();
}
