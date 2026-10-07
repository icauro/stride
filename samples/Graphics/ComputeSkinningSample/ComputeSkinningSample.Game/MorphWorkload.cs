namespace ComputeSkinningSample;

// No engine dependency: weights are generated from a seed, so every run replays identical frames.
public sealed class MorphWorkload
{
    public const int TargetCount = 600;
    public const int DefaultSeed = 12345;
    public static readonly string[] Scenarios = ["all-changing", "mixed-200-200-200"];
    public static readonly (string Name, int Count)[] TargetGroups = [("Localized", 200), ("Medium", 200), ("Global", 200)];
    public static readonly string[] DefaultTargetNames = TargetGroups.SelectMany(group => Enumerable.Range(0, group.Count).Select(index => $"{group.Name}_{index:D4}")).ToArray();

    private readonly float[] weights;
    private readonly int[] changingTargets;
    public string Name { get; }
    public string[] TargetNames => DefaultTargetNames;
    public int FrameCount { get; private set; }
    public int InstanceCount { get; }
    public int WarmupFrames { get; }
    public int MeasuredFrames => FrameCount - WarmupFrames;

    public void LimitMeasuredFrames(int count)
    {
        if (count > 0 && count < MeasuredFrames) FrameCount = WarmupFrames + count;
    }

    public MorphWorkload(string caseName, int instanceCount = 16, int warmupFrames = 64, int measuredFrames = 128, int seed = DefaultSeed)
    {
        int caseIndex = Array.IndexOf(Scenarios, caseName);
        if (caseIndex < 0) throw new ArgumentException($"Unknown scenario {caseName}.");
        if (instanceCount < 1 || warmupFrames < 0 || measuredFrames < 1) throw new ArgumentOutOfRangeException(nameof(instanceCount));
        Name = caseName;
        InstanceCount = instanceCount;
        WarmupFrames = warmupFrames;
        FrameCount = warmupFrames + measuredFrames;

        var shuffled = Enumerable.Range(0, TargetCount).ToArray();
        var random = new Random32((uint)seed);
        for (int i = TargetCount - 1; i > 0; i--)
        {
            int j = (int)(random.Next() % (uint)(i + 1));
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }
        int[] Slice(int start) => shuffled.Skip(start).Take(200).Order().ToArray();
        int[] fixedTargets = caseIndex == 0 ? [] : Slice(0);
        changingTargets = caseIndex == 0 ? Enumerable.Range(0, TargetCount).ToArray() : Slice(200);

        var generators = Enumerable.Range(0, instanceCount)
            .Select(instance => new Random32((uint)seed ^ (0x9e3779b9u * (uint)(caseIndex + 1)) ^ (0x85ebca6bu * (uint)instance))).ToArray();
        var rows = new float[instanceCount][];
        for (int instance = 0; instance < instanceCount; instance++)
        {
            rows[instance] = new float[TargetCount];
            foreach (int target in fixedTargets) rows[instance][target] = generators[instance].Weight(0);
        }
        weights = new float[(long)FrameCount * instanceCount * TargetCount];
        for (int frame = 0; frame < FrameCount; frame++)
            for (int instance = 0; instance < instanceCount; instance++)
            {
                var row = rows[instance];
                foreach (int target in changingTargets) row[target] = generators[instance].Weight(row[target]);
                row.CopyTo(weights.AsSpan((frame * instanceCount + instance) * TargetCount, TargetCount));
            }
    }

    // Resolve these names to each implementation's target indices once, before timing.
    public int[] MapTargets(IReadOnlyList<string> importedNames)
    {
        var indices = importedNames.Select((name, index) => (name, index)).ToDictionary(x => x.name, x => x.index, StringComparer.Ordinal);
        return TargetNames.Select(name => indices.TryGetValue(name, out int index) ? index : throw new InvalidDataException($"Missing target: {name}")).ToArray();
    }

    public ReadOnlySpan<float> GetFrame(int frame, int instance)
    {
        if ((uint)frame >= (uint)FrameCount) throw new ArgumentOutOfRangeException(nameof(frame));
        if ((uint)instance >= (uint)InstanceCount) throw new ArgumentOutOfRangeException(nameof(instance));
        return weights.AsSpan((frame * InstanceCount + instance) * TargetCount, TargetCount);
    }

    // Call once per simulated frame. First/reset frames write all weights; subsequent
    // frames write only changing targets, leaving static/zero weights undisturbed.
    // Each instance owns its own previousFrame value; the workload can be shared.
    public void ApplyFrame(int frame, int instance, ref int previousFrame, Action<int, float> setWeight)
    {
        var row = GetFrame(frame, instance);
        if (frame != previousFrame + 1 || previousFrame < 0 || frame == 0)
            for (int target = 0; target < TargetCount; target++) setWeight(target, row[target]);
        else
            foreach (int target in changingTargets) setWeight(target, row[target]);
        previousFrame = frame;
    }

    // Explicit xorshift32; does not depend on the .NET random implementation.
    public struct Random32(uint seed)
    {
        private uint state = seed == 0 ? 1 : seed;

        public uint Next()
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        public float NextFloat() => Next() / 4294967296f;

        // Exactly representable values in (0, 1], guaranteed to change.
        public float Weight(float previous)
        {
            float value = ((Next() & 0x7fffff) + 1) / 8388608f;
            return value != previous ? value : value == 1 ? 1 / 8388608f : value + 1 / 8388608f;
        }
    }
}
