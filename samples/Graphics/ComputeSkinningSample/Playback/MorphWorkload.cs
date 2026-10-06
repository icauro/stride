using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace MorphBenchmark;

// No engine dependency: both branches read these exact Float32 frames.
public sealed class MorphWorkload
{
    private readonly float[] weights;
    private readonly int[] changingTargets;
    public string Name { get; }
    public string[] TargetNames { get; }
    public int FrameCount { get; }
    public int InstanceCount { get; }
    public int WarmupFrames { get; }
    public int MeasuredFrames => FrameCount - WarmupFrames;

    public MorphWorkload(string manifestPath, string caseName)
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Little endian required.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = manifest.RootElement;
        if (root.GetProperty("version").GetInt32() != 2 || root.GetProperty("weightEncoding").GetString() != "little-endian-float32-frame-instance-target")
            throw new InvalidDataException("Unsupported workload format.");
        InstanceCount = root.GetProperty("instanceCount").GetInt32();
        if (InstanceCount < 1 || InstanceCount > 16 || root.GetProperty("meshSharing").GetString() != "one-model-shared-by-all-entities")
            throw new InvalidDataException("Expected 1 to 16 entities sharing one model.");
        Name = caseName;
        TargetNames = root.GetProperty("targetNames").EnumerateArray().Select(x => x.GetString()!).ToArray();
        if (TargetNames.Length != 600 || root.GetProperty("targetCount").GetInt32() != 600 || TargetNames.Any(string.IsNullOrWhiteSpace) || TargetNames.Distinct().Count() != 600)
            throw new InvalidDataException("Expected 600 unique target names.");
        FrameCount = root.GetProperty("frameCount").GetInt32();
        WarmupFrames = root.GetProperty("warmupFrames").GetInt32();
        if (FrameCount <= WarmupFrames || WarmupFrames < 0 || root.GetProperty("measuredFrames").GetInt32() != MeasuredFrames)
            throw new InvalidDataException("Invalid frame counts.");
        var selected = root.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == caseName);
        changingTargets = selected.GetProperty("changingTargets").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        var fixedTargets = selected.GetProperty("staticTargets").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        var zeroTargets = selected.GetProperty("zeroTargets").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        if (!fixedTargets.Concat(changingTargets).Concat(zeroTargets).Order().SequenceEqual(Enumerable.Range(0, 600)))
            throw new InvalidDataException("Invalid target partition.");
        string directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        string file = selected.GetProperty("weightsFile").GetString()!;
        if (Path.GetFileName(file) != file) throw new InvalidDataException("Weight file must be beside the manifest.");
        byte[] bytes = File.ReadAllBytes(Path.Combine(directory, file));
        if (bytes.LongLength != (long)FrameCount * InstanceCount * 600 * sizeof(float) ||
            !Convert.ToHexString(SHA256.HashData(bytes)).Equals(selected.GetProperty("weightsSha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Weight file length or checksum mismatch.");
        weights = MemoryMarshal.Cast<byte, float>(bytes).ToArray();
        if (weights.Any(x => !float.IsFinite(x) || x < 0 || x > 1)) throw new InvalidDataException("Invalid weight value.");
        for (int frame = 0; frame < FrameCount; frame++)
        for (int instance = 0; instance < InstanceCount; instance++)
        {
            var row = GetFrame(frame, instance);
            foreach (int target in zeroTargets) if (row[target] != 0) throw new InvalidDataException("Zero target changed.");
            foreach (int target in fixedTargets)
                if (row[target] <= 0 || row[target] != GetFrame(0, instance)[target]) throw new InvalidDataException("Invalid static target.");
            foreach (int target in changingTargets)
                if (row[target] <= 0 || frame > 0 && row[target] == GetFrame(frame - 1, instance)[target]) throw new InvalidDataException("Invalid changing target.");
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
        return weights.AsSpan((frame * InstanceCount + instance) * 600, 600);
    }

    // Call once per simulated frame. First/reset frames write all weights; subsequent
    // frames write only changing targets, leaving static/zero weights undisturbed.
    // Each instance owns its own previousFrame value; the workload can be shared.
    public void ApplyFrame(int frame, int instance, ref int previousFrame, Action<int, float> setWeight)
    {
        var row = GetFrame(frame, instance);
        if (frame != previousFrame + 1 || previousFrame < 0 || frame == 0)
            for (int target = 0; target < 600; target++) setWeight(target, row[target]);
        else
            foreach (int target in changingTargets) setWeight(target, row[target]);
        previousFrame = frame;
    }

    public static void VerifyAsset(string manifestPath, string assetPath)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        using var stream = File.OpenRead(assetPath);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(manifest.RootElement.GetProperty("assetSha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Benchmark mesh differs from the fixture.");
    }
}
