using Stride.Animations;

namespace Stride.Editor.ImGui;

internal static class CurveSamplingKeys
{
    internal static IEnumerable<float> Enumerate<T>(IComputeCurve<T> root) where T : struct
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<IComputeCurve<T>>(); pending.Push(root);
        while (pending.TryPop(out var curve))
        {
            if (!visited.Add(curve)) continue;
            if (curve is ComputeAnimationCurve<T> animation)
                foreach (var point in animation.KeyFrames) yield return point.Key;
            if (curve is ComputeBinaryCurve<T> binary)
            {
                if (binary.RightChild != null) pending.Push(binary.RightChild);
                if (binary.LeftChild != null) pending.Push(binary.LeftChild);
            }
        }
    }
}
