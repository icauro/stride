using System.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed record SceneDrawSummary(int Frames, long DrawCalls, double MedianMilliseconds, double P95Milliseconds, double MeanMilliseconds, double[] SortedCpuSamplesMilliseconds);
internal sealed class SceneDrawMeasurements
{
    private readonly List<(double Time, int Calls)> samples = [];
    public bool Recording { get; private set; }
    public int Count => samples.Count;
    public void Begin() { samples.Clear(); Recording = true; }
    public void Record(long start, int calls)
    {
        if (Recording) samples.Add((Stopwatch.GetElapsedTime(start).TotalMilliseconds, calls));
    }
    public SceneDrawSummary End()
    {
        Recording = false;
        var times = samples.Select(sample => sample.Time).Order().ToArray();
        if (times.Length == 0) throw new InvalidOperationException("No scene draw frames were sampled.");
        return new SceneDrawSummary(times.Length, samples.Sum(sample => (long)sample.Calls),
            (times[(times.Length - 1) / 2] + times[times.Length / 2]) / 2,
            times[Math.Max(0, (int)Math.Ceiling(times.Length * 0.95) - 1)], times.Average(), times);
    }
}
