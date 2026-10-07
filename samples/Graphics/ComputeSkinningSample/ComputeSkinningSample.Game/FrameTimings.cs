using System.Diagnostics;
using System.Reflection;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Stride.UI.Controls;

namespace ComputeSkinningSample;

/// <summary>
/// Live CPU/GPU timings for the sample scenes. GPU regions use Stride timestamp query pools and are read back
/// a few frames later without stalling. The engine's deformation renderer reports its own regions
/// (ControlUpload, DenseSkinning, SparseSkinning) through <see cref="IGpuTimestampRecorder"/>.
/// </summary>
public sealed class FrameTimings : IGpuTimestampRecorder, IDisposable
{
    private const int Slots = 6, Capacity = 512, AverageFrames = 30;
    // Vulkan pools must be reset before reuse; Direct3D 11 has no reset call.
    private static readonly MethodInfo ResetQueryPool = typeof(CommandList).GetMethod("ResetQueryPool", [typeof(QueryPool)]);

    private readonly ScriptComponent owner;
    private readonly CommandList commandList;
    private readonly QueryPool[] pools = new QueryPool[Slots];
    private readonly List<(string Name, int Start, int End)>[] regions = new List<(string, int, int)>[Slots];
    private readonly int[] used = new int[Slots];
    private readonly bool[] pending = new bool[Slots];
    private readonly long[] values = new long[Capacity];
    private readonly Dictionary<string, Queue<double>> gpu = new(), cpu = new();
    private readonly BoundaryProcessor begin, end;
    private readonly TimedSceneRenderer sceneRenderer;
    private readonly ISceneRenderer originalRenderer;
    private int slot = -1, next;
    private bool disposed;
    private long deformationCpuStart;

    public FrameTimings(ScriptComponent owner)
    {
        this.owner = owner;
        commandList = owner.Game.GraphicsContext.CommandList;
        for (int i = 0; i < Slots; i++) { pools[i] = QueryPool.New(owner.GraphicsDevice, QueryType.Timestamp, Capacity); regions[i] = new(); }
        // During a scene switch the previous scene's timings are still alive until its script is cancelled.
        owner.Services.RemoveService<IGpuTimestampRecorder>();
        owner.Services.AddService<IGpuTimestampRecorder>(this);
        // Bracket the model processors' GPU recording, where deformation is dispatched.
        begin = new BoundaryProcessor(this, true, -1);
        end = new BoundaryProcessor(this, false, 1);
        owner.SceneSystem.SceneInstance.Processors.Add(begin);
        owner.SceneSystem.SceneInstance.Processors.Add(end);
        var compositor = owner.SceneSystem.GraphicsCompositor;
        originalRenderer = compositor.Game;
        while (originalRenderer is TimedSceneRenderer timed)
            originalRenderer = timed.Child;
        compositor.Game = sceneRenderer = new TimedSceneRenderer { Child = originalRenderer, Timings = this };
    }

    public IDisposable BeginRegion(string name)
    {
        if (disposed || slot < 0 || used[slot] + 2 > Capacity) return null;
        int start = used[slot]++;
        commandList.WriteTimestamp(pools[slot], start);
        return new Region(this, name, slot, start);
    }

    private void BeginFrame()
    {
        if (disposed) return;
        Poll();
        slot = -1;
        if (pending[next]) return; // GPU is far behind; skip timing this frame rather than stall.
        slot = next;
        next = (next + 1) % Slots;
        ResetQueryPool?.Invoke(commandList, [pools[slot]]);
        used[slot] = 0;
        regions[slot].Clear();
        frameRegion = BeginRegion("Frame");
    }

    private IDisposable frameRegion;

    private void EndFrame()
    {
        if (disposed || slot < 0) return;
        frameRegion?.Dispose();
        frameRegion = null;
        // Vulkan only reports a pool once every query in it has been written.
        for (int index = used[slot]; index < Capacity; index++) commandList.WriteTimestamp(pools[slot], index);
        pending[slot] = true;
        slot = -1;
    }

    private void Poll()
    {
        for (int i = 0; i < Slots; i++)
        {
            if (!pending[i] || !pools[i].TryGetData(values)) continue;
            pending[i] = false;
            double frequency = owner.GraphicsDevice.TimestampFrequency;
            if (frequency <= 0) continue;
            var sums = new Dictionary<string, double>();
            foreach (var (name, start, stop) in regions[i])
                sums[name] = sums.GetValueOrDefault(name) + (values[stop] - values[start]) * 1000.0 / frequency;
            foreach (var (name, ms) in sums) Add(gpu, name, ms);
        }
    }

    private static void Add(Dictionary<string, Queue<double>> samples, string name, double value)
    {
        if (!samples.TryGetValue(name, out var queue)) samples[name] = queue = new Queue<double>();
        queue.Enqueue(value);
        while (queue.Count > AverageFrames) queue.Dequeue();
    }

    private static string Format(Dictionary<string, Queue<double>> samples, string name)
        => samples.TryGetValue(name, out var queue) && queue.Count > 0 ? $"{queue.Average():0.00} ms" : "-";

    /// <summary>Row names for <see cref="Report"/>; indented rows are breakdowns of the row above.</summary>
    public static readonly string[] Rows =
    [
        "Frame rate", "CPU frame", "CPU deformation", "CPU scene draw",
        "GPU frame", "GPU deformation", "   upload", "   dense dispatch", "   sparse dispatch", "GPU scene",
    ];

    /// <summary>Call once per frame from the owning script's Update; fills one value per <see cref="Rows"/> entry.</summary>
    public void Report(TextBlock[] values)
    {
        Add(cpu, "Frame", owner.Game.UpdateTime.Elapsed.TotalMilliseconds);
        values[0].Text = $"{owner.Game.UpdateTime.FramePerSecond:0} FPS";
        values[1].Text = Format(cpu, "Frame");
        values[2].Text = Format(cpu, "Deformation");
        values[3].Text = Format(cpu, "Scene");
        values[4].Text = Format(gpu, "Frame");
        values[5].Text = Format(gpu, "Deformation");
        values[6].Text = Format(gpu, "ControlUpload");
        values[7].Text = Format(gpu, "DenseSkinning");
        values[8].Text = Format(gpu, "SparseSkinning");
        values[9].Text = Format(gpu, "Scene");
    }

    public void Dispose()
    {
        disposed = true;
        if (owner.Services.GetService<IGpuTimestampRecorder>() == this)
            owner.Services.RemoveService<IGpuTimestampRecorder>();
        owner.SceneSystem.SceneInstance?.Processors.Remove(begin);
        owner.SceneSystem.SceneInstance?.Processors.Remove(end);
        var compositor = owner.SceneSystem.GraphicsCompositor;
        if (compositor != null && compositor.Game == sceneRenderer) compositor.Game = originalRenderer;
        foreach (var pool in pools) pool?.Dispose();
    }

    private sealed class Region(FrameTimings timings, string name, int slot, int start) : IDisposable
    {
        public void Dispose()
        {
            if (timings.disposed || timings.slot != slot || timings.used[slot] >= Capacity) return;
            int stop = timings.used[slot]++;
            timings.commandList.WriteTimestamp(timings.pools[slot], stop);
            timings.regions[slot].Add((name, start, stop));
        }
    }

    private sealed class BoundaryProcessor : EntityProcessor<ModelComponent, ModelComponent>
    {
        private readonly FrameTimings timings;
        private readonly bool begin;
        private IDisposable deformation;

        public BoundaryProcessor(FrameTimings timings, bool begin, int order) { this.timings = timings; this.begin = begin; Order = order; }
        protected override ModelComponent GenerateComponentData(Entity entity, ModelComponent component) => component;
        protected override bool IsAssociatedDataValid(Entity entity, ModelComponent component, ModelComponent data) => component == data;

        public override void Draw(RenderContext context)
        {
            if (begin)
            {
                timings.BeginFrame();
                timings.deformationCpuStart = Stopwatch.GetTimestamp();
                timings.end.deformation = timings.BeginRegion("Deformation");
                return;
            }
            deformation?.Dispose();
            deformation = null;
            Add(timings.cpu, "Deformation", Stopwatch.GetElapsedTime(timings.deformationCpuStart).TotalMilliseconds);
        }
    }

    private sealed class TimedSceneRenderer : SceneRendererBase
    {
        public ISceneRenderer Child;
        public FrameTimings Timings;

        protected override void CollectCore(RenderContext context)
        {
            base.CollectCore(context);
            Child?.Collect(context);
        }

        protected override void DrawCore(RenderContext context, RenderDrawContext drawContext)
        {
            long start = Stopwatch.GetTimestamp();
            using (Timings.BeginRegion("Scene")) Child?.Draw(drawContext);
            Add(Timings.cpu, "Scene", Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            Timings.EndFrame();
        }
    }
}
