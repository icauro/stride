using System.Diagnostics;
using System.Text.Json;
using ComputeSkinningSample;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Graphics.Data;
using Stride.Graphics.Semantics;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Stride.Rendering.Lights;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Shaders.Compiler;
using Buffer = Stride.Graphics.Buffer;

// Automated timed runs: launch the Windows app with --benchmark [options].
static class BenchmarkRunner
{
    public static readonly Stopwatch Startup = Stopwatch.StartNew();
    public static string Elapsed => $"[{Startup.Elapsed.TotalSeconds:F1}s]";

    public static void Run(string[] args)
    {
        string Value(string option, string fallback)
        {
            int index = Array.IndexOf(args, option);
            return index < 0 ? fallback : index + 1 < args.Length ? args[index + 1] : throw new ArgumentException($"Missing {option} value.");
        }
        string scenario = Value("--scenario", "both");
        if (scenario is not ("both" or "all-changing" or "mixed-200-200-200")) throw new ArgumentException("Unknown scenario.");
        BenchmarkGame.MeasuredFrameLimit = int.Parse(Value("--measured-frames", "128"));
        BenchmarkGame.MeshCount = int.Parse(Value("--meshes", "2"));
        BenchmarkGame.InstancesPerMesh = int.Parse(Value("--instances-per-mesh", "32"));
        using var game = new BenchmarkGame(scenario, Value("--output", Path.Combine(AppContext.BaseDirectory, "results")), args.Contains("--preview"), Value("--mode", "sparse"), !args.Contains("--morph-only"), args.Contains("--debug-gpu"), args.Contains("--verify-lifecycle"), args.Contains("--frustum-culling"));
        game.RenderDocCapture = args.Contains("--renderdoc-capture");
        game.SkinningOnly = args.Contains("--skinning-only");
        game.VertexShaderSkinning = args.Contains("--vertex-skinning");
        game.ZeroMorphs = args.Contains("--zero-morphs");
        game.PointShadows = args.Contains("--point-shadows");
        game.DirectionalShadows = args.Contains("--directional-shadows");
        game.PointLights = game.PointShadows || args.Contains("--point-lights");
        game.DeformationBatchSize = int.Parse(Value("--batch", "32"));
        game.DeformationThreadGroup = Value("--thread-group", "X32Y16");
        game.Run();
    }
}

sealed partial class BenchmarkGame : Game
{
    public bool RenderDocCapture { get; set; }
    public bool SkinningOnly { get; set; }
    public bool VertexShaderSkinning { get; set; }
    public static int MeasuredFrameLimit { get; set; } = 128;
    public static int MeshCount { get; set; } = 2;
    public static int InstancesPerMesh { get; set; } = 32;
    public int DeformationBatchSize { get; set; } = 32;
    public string DeformationThreadGroup { get; set; } = "X32Y16";
    public bool ZeroMorphs { get; set; }
    public bool PointShadows { get; set; }
    public bool DirectionalShadows { get; set; }
    public bool PointLights { get; set; }
    private TimedShadowMapRenderer timedShadows;
    private readonly string output;
    private readonly bool preview;
    private readonly bool animateSkinning;
    private readonly string mode;
    private readonly ModelComponent[] models;
    private int[] animatedNodes;
    private Quaternion[] bindRotations;
    private bool gpuVerified;
    private readonly bool verifyLifecycle;
    private readonly Entity[] entities;
    private readonly MorphWorkload[] workloads;
    private readonly Action<int, float>[] setters;
    private readonly int[] previous;
    private readonly int InstanceCount;
    private readonly List<Buffer> buffers = [];
    private readonly List<double> updateMs = [], drawMs = [];
    private int caseIndex, frame = -1;
    private Model sharedModel;
    private Model[] sharedModels;
    private GpuTimers gpuTimers;
    private bool waitingForGpu;
    private long gpuDrainStarted;
    private readonly bool frustumCulling;
    private BenchmarkCameraRenderer benchmarkCamera;

    public BenchmarkGame(string scenario, string output, bool preview, string mode, bool animateSkinning, bool debugGpu, bool verifyLifecycle, bool frustumCulling)
    {
        if (mode is not ("dense" or "sparse")) throw new ArgumentException("Mode must be dense or sparse.");
        this.mode = mode;
        this.animateSkinning = animateSkinning;
        this.verifyLifecycle = verifyLifecycle;
        this.frustumCulling = frustumCulling;
        this.output = output;
        this.preview = preview;
        workloads = (scenario == "both" ? MorphWorkload.Scenarios : new[] { scenario })
            .Select(name => new MorphWorkload(name, MeshCount * InstancesPerMesh)).ToArray();
        foreach (var workload in workloads) workload.LimitMeasuredFrames(MeasuredFrameLimit);
        InstanceCount = workloads[0].InstanceCount;
        models = new ModelComponent[InstanceCount]; entities = new Entity[InstanceCount];
        setters = new Action<int, float>[InstanceCount];
        previous = Enumerable.Repeat(-1, InstanceCount).ToArray();
        lastOutputs = new Stride.Graphics.Buffer[InstanceCount];
        IsFixedTimeStep = false;
        AutoLoadDefaultSettings = false;
        GraphicsDeviceManager.SynchronizeWithVerticalRetrace = false;
        GraphicsDeviceManager.PreferredBackBufferWidth = 1280;
        GraphicsDeviceManager.PreferredBackBufferHeight = 720;
        GraphicsDeviceManager.PreferredGraphicsProfile = [GraphicsProfile.Level_11_0];
        GraphicsDeviceManager.ShaderProfile = GraphicsProfile.Level_11_0;
        if (debugGpu) GraphicsDeviceManager.DeviceCreationFlags |= DeviceCreationFlags.Debug;
    }

    protected override Task LoadContent()
    {
#if !VULKAN
        GraphicsDevice.DeviceInfoQueueMessage += (ref readonly Silk.NET.Direct3D11.Message message, string description) => Console.WriteLine("GPU: " + description);
#endif
        // Standalone scene has no compiled asset package; use the matching branch's
        // shader sources copied beside the executable, with the normal effect cache.
        var compiler = new EffectCompiler(Content.FileProvider) { UseFileSystem = true };
        compiler.SourceDirectories.Add(Path.Combine(AppContext.BaseDirectory, "shaders"));
        EffectSystem.Compiler = new EffectCompilerCache(compiler, Content.FileProvider as Stride.Core.IO.DatabaseFileProvider);
        Console.WriteLine($"{BenchmarkRunner.Elapsed} LoadContent");
        var generation = Stopwatch.StartNew();
        // Each model gets its own seed, so their morph bands differ.
        sharedModels = Enumerable.Range(0, MeshCount).Select(index => ProceduralCylinder.Create(GraphicsDevice, mode == "dense" ? MeshMorphLayout.DenseMorphMajor : MeshMorphLayout.SparseVertexMajor, seed: MorphWorkload.DefaultSeed + index)).ToArray();
        sharedModel = sharedModels[0];
        Console.WriteLine($"{BenchmarkRunner.Elapsed} Generated {MeshCount} procedural cylinders in {generation.Elapsed.TotalSeconds:F1} s.");
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var mesh in sharedModels.SelectMany(model => model.Meshes))
        {
            var binding = mesh.Draw.VertexBuffers[0];
            buffers.Add(binding.Buffer);
            buffers.Add(mesh.Draw.IndexBuffer.Buffer);
            var positions = new Vector3[binding.Count];
            var reader = new VertexBufferHelper(binding, binding.Buffer.GetSerializationData().Content, out _);
            reader.Copy<PositionSemantic, Vector3>(positions);
            Vector3 meshMin = new(float.MaxValue), meshMax = new(float.MinValue);
            foreach (var position in positions) { meshMin = Vector3.Min(meshMin, position); meshMax = Vector3.Max(meshMax, position); }
            mesh.BoundingBox = new BoundingBox(meshMin, meshMax);
            mesh.BoundingSphere = BoundingSphere.FromBox(mesh.BoundingBox);
            min = Vector3.Min(min, meshMin); max = Vector3.Max(max, meshMax);
            if (!animateSkinning) { mesh.Skinning = null; mesh.NodeIndex = 0; }
            mesh.MaterialIndex = 0;
        }
        if (!animateSkinning) foreach (var model in sharedModels) model.Skeleton = null;
        animatedNodes = sharedModel.Meshes.SelectMany(mesh => mesh.Skinning?.Bones.Select(bone => bone.NodeIndex) ?? Enumerable.Empty<int>()).Where(node => node != 0).Distinct().ToArray();
        if (animateSkinning && animatedNodes.Length == 0) throw new InvalidDataException("The benchmark mesh has no skeleton/bones.");
        bindRotations = animatedNodes.Select(node => sharedModel.Skeleton.Nodes[node].Transform.Rotation).ToArray();
        Color4[] colors = [new(0.3f, 0.65f, 0.9f, 1), new(0.9f, 0.55f, 0.3f, 1), new(0.45f, 0.85f, 0.4f, 1), new(0.8f, 0.4f, 0.85f, 1)];
        for (int index = 0; index < sharedModels.Length; index++)
        {
            var model = sharedModels[index];
            model.BoundingBox = new BoundingBox(min, max);
            model.BoundingSphere = BoundingSphere.FromBox(model.BoundingBox);
            model.Materials.Clear();
            model.Materials.Add(Material.New(GraphicsDevice, new MaterialDescriptor {
                Attributes = { Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(colors[index % colors.Length])), DiffuseModel = new MaterialDiffuseLambertModelFeature() } }));
        }

        var scene = SceneSystem.SceneInstance.RootScene;
        Vector3 center = (min + max) * 0.5f;
        float extent = Math.Max((max - min).Length(), 1);
        int gridSize = (int)Math.Ceiling(Math.Sqrt(InstanceCount));
        for (int instance = 0; instance < InstanceCount; instance++)
        {
            var instanceModel = sharedModels[instance / InstancesPerMesh];
            var modelComponent = new ModelComponent(instanceModel) { IsShadowCaster = PointShadows || DirectionalShadows };
            models[instance] = modelComponent;
            var entity = new Entity($"Morph benchmark {instance}") { modelComponent };
            entities[instance] = entity;
            entity.Transform.Position = -center + new Vector3((instance % gridSize - (gridSize - 1) * 0.5f) * extent * 1.5f, (instance / gridSize - (gridSize - 1) * 0.5f) * extent * 1.5f, 0);
            scene.Entities.Add(entity);
            var mappings = instanceModel.Meshes.Select(mesh => workloads[0].MapTargets(mesh.MorphTargets.TargetNames)).ToArray();
            setters[instance] = (target, weight) => {
                for (int mesh = 0; mesh < mappings.Length; mesh++) modelComponent.SetMorphWeight(mesh, mappings[mesh][target], weight);
            };
            if (!ReferenceEquals(modelComponent.Model, instanceModel)) throw new InvalidOperationException("Model is not shared.");
        }
        if (ZeroMorphs)
            for (int instance = 0; instance < InstanceCount; instance++)
            {
                models[instance].SetAllMorphWeights(0f);
                models[instance].Morphs.Enabled = false;
                setters[instance] = (_, _) => { };
            }
        if (SkinningOnly)
        {
            foreach (var mesh in sharedModels.SelectMany(model => model.Meshes)) mesh.MorphTargets = null;
            for (int instance = 0; instance < InstanceCount; instance++)
            {
                models[instance].Morphs.Enabled = false;
                setters[instance] = (_, _) => { };
            }
        }
        var camera = new CameraComponent { NearClipPlane = extent * 0.01f, FarClipPlane = extent * 100 };
        var cameraEntity = new Entity("Benchmark camera") { camera };
        cameraEntity.Transform.Position = new Vector3(0, 0, extent * 2.5f * (float)Math.Ceiling(Math.Sqrt(InstanceCount)));
        scene.Entities.Add(cameraEntity);
        scene.Entities.Add(new Entity("Ambient") { new LightComponent { Type = new LightAmbient(), Intensity = 0.5f } });
        var light = new Entity("Directional") { new LightComponent { Type = new LightDirectional { Shadow = { Enabled = DirectionalShadows, Size = LightShadowMapSize.Large } }, Intensity = 1 } };
        light.Transform.Rotation = Quaternion.RotationYawPitchRoll(-0.5f, -0.6f, 0);
        scene.Entities.Add(light);
        if (PointLights)
        {
            Vector3[] positions = [new(0.98857665f, 5.111102f, 5.578897f), new(0.98857665f, 5.111102f, -0.6172457f), new(-2.8989758f, 5.111102f, 0), new(3.1172156f, 5.111102f, 0)];
            foreach (var position in positions)
            {
                var point = new Entity("Point light") { new LightComponent { Type = new LightPoint { Radius = 12f, Shadow = { Enabled = PointShadows, Size = LightShadowMapSize.Small } } } };
                point.Transform.Position = position;
                scene.Entities.Add(point);
            }
        }
        SceneSystem.GraphicsCompositor = GraphicsCompositorHelper.CreateDefault(false, camera: camera, clearColor: new Color4(0.03f, 0.04f, 0.06f, 1), graphicsProfile: GraphicsProfile.Level_11_0);
        var previousCamera = (SceneCameraRenderer)SceneSystem.GraphicsCompositor.Game;
        benchmarkCamera = new BenchmarkCameraRenderer(!frustumCulling) { Camera = previousCamera.Camera, Child = previousCamera.Child, RenderMask = previousCamera.RenderMask };
        SceneSystem.GraphicsCompositor.Game = benchmarkCamera;
        gpuTimers = new GpuTimers(GraphicsDevice, GraphicsContext, mode);
        var lighting = SceneSystem.GraphicsCompositor.RenderSystem.RenderFeatures.OfType<MeshRenderFeature>().Single().RenderFeatures.OfType<ForwardLightingRenderFeature>().Single();
        timedShadows = new TimedShadowMapRenderer(lighting.ShadowMapRenderer, gpuTimers);
        lighting.ShadowMapRenderer = timedShadows;
        Services.AddService<IGpuTimestampRecorder>(gpuTimers);
        const int deformationOrder = 0;
        SceneSystem.SceneInstance.Processors.Add(new GpuTimingBoundaryProcessor(gpuTimers, true, deformationOrder - 1));
        SceneSystem.SceneInstance.Processors.Add(new GpuTimingBoundaryProcessor(gpuTimers, false, deformationOrder + 1));
        Console.WriteLine($"Deformation: batch size {DeformationBatchSize}, thread group {DeformationThreadGroup}.");
        Console.WriteLine($"{BenchmarkRunner.Elapsed} Scene ready: {InstanceCount} entities, {MeshCount} shared models x {InstancesPerMesh}, {sharedModel.Meshes.Sum(mesh => mesh.Draw.VertexBuffers[0].Count)} vertices per model, {MorphWorkload.TargetCount} targets each.");
        return Task.CompletedTask;
    }

    protected override void Update(GameTime gameTime)
    {
        if (sharedModel == null) { base.Update(gameTime); return; }
        if (waitingForGpu) { base.Update(gameTime); return; }
        var workload = workloads[caseIndex];
        frame++;
        ApplyDeformationSettings();
        if (verifyLifecycle) ApplyLifecycleChanges();
        long start = Stopwatch.GetTimestamp();
        for (int instance = 0; instance < InstanceCount; instance++)
            if (entities[instance].Get<ModelComponent>() == models[instance]) workload.ApplyFrame(frame, instance, ref previous[instance], setters[instance]);
        if (zeroWeightsThisFrame)
        {
            models[0].SetAllMorphWeights(0);
            zeroWeightsThisFrame = false;
        }
        if (animateSkinning)
            for (int instance = 0; instance < InstanceCount; instance++)
                for (int bone = 0; bone < animatedNodes.Length; bone++)
                    models[instance].Skeleton.NodeTransformations[animatedNodes[bone]].Transform.Rotation = bindRotations[bone] * Quaternion.RotationZ((float)Math.Sin(frame / 60.0 + bone * 0.7) * 0.18f);
        // Apply before engine update so processors see this frame's weights.
        base.Update(gameTime);
        if (frame >= workload.WarmupFrames) updateMs.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        if (frame % 10 == 0) Window.Title = $"{mode} | {workload.Name} | {InstanceCount} entities | {frame + 1}/{workload.FrameCount} | {(animateSkinning ? "fused morph + skinning" : "morph only")}";
    }

    protected override void Draw(GameTime gameTime)
    {
        if (RenderDocCapture && frame == 120) RenderDocDiagnostic.Trigger();
        if (RenderDocCapture && frame == 124) { Exit(); return; }
        if (waitingForGpu)
        {
            // Continue presenting without recording while final asynchronous samples
            // complete. The model stays at the last prerecorded frame.
            base.Draw(gameTime);
            gpuTimers.Poll(drain: true);
            if (gpuTimers.HasPending(caseIndex))
            {
                if (Stopwatch.GetElapsedTime(gpuDrainStarted).TotalSeconds > 10) throw new TimeoutException("GPU timing results did not complete within 10 seconds.");
                return;
            }
            FinishScenario();
            return;
        }
        bool recordGpu = sharedModel != null && frame >= 0 && !verifyLifecycle;
        if (recordGpu) gpuTimers.BeginFrame(caseIndex, frame, frame >= workloads[caseIndex].WarmupFrames);
        long start = Stopwatch.GetTimestamp();
        try { base.Draw(gameTime); }
        finally { if (recordGpu) gpuTimers.EndFrame(); }
        if (sharedModel == null || frame < 0) return;
        var workload = workloads[caseIndex];
        if (frame >= workload.WarmupFrames) drawMs.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        if (frame == Math.Max(workload.WarmupFrames - 1, 0) || frame + 1 == workload.FrameCount)
        {
            Console.WriteLine($"{BenchmarkRunner.Elapsed} Visible entities: {benchmarkCamera.VisibleEntities}/{InstanceCount}; culled: {string.Join(", ", benchmarkCamera.CulledEntities)}");
            if (!frustumCulling && benchmarkCamera.VisibleEntities != InstanceCount) throw new Exception("Benchmark did not render all configured entities.");
        }
        if (verifyLifecycle)
        {
            if (frame is 12 or 18 or 21 or 25 or 29 or 32 or 35 or 38 or 41 or 45 or 49 or 57) VerifyGpuOutput();
            if (frame is 47 or 54) VerifyModelOutputReleased();
            if (frame == 60) { Console.WriteLine("PASS model deformation lifecycle: zero weights, VS/compute switch, morph controls disable/re-enable, model disable/remove/re-add and replacement with GPU-only source."); Exit(); }
            return;
        }
        if (frame == Math.Max(workload.WarmupFrames - 1, 0) || frame + 1 == workload.FrameCount)
            VerifyGpuOutput(); // Readback is excluded from the timing interval.
        if (frame + 1 < workload.FrameCount) return;
        waitingForGpu = true;
        gpuDrainStarted = Stopwatch.GetTimestamp();
        gpuTimers.Poll(drain: true);
        if (!gpuTimers.HasPending(caseIndex)) FinishScenario();
    }

    // The scene creates its ModelRenderProcessor only after the first update, so settings cannot be applied in LoadContent.
    private void ApplyDeformationSettings()
    {
        var processors = SceneSystem.SceneInstance.Processors.OfType<ModelRenderProcessor>().ToArray();
        if (processors.Length == 0)
        {
            if (frame > 0) throw new InvalidOperationException("ModelRenderProcessor was never created; deformation settings not applied.");
            return;
        }
        deformationSettings ??= new MeshDeformationSettings
        {
            Mode = VertexShaderSkinning ? MeshDeformationMode.ComputeMorph : MeshDeformationMode.Compute,
            BatchSize = DeformationBatchSize,
            ThreadGroup = Enum.Parse<DeformationThreadGroup>(DeformationThreadGroup),
        };
        foreach (var processor in processors)
        {
            if (processor.DeformationSettings == deformationSettings) continue;
            processor.DeformationSettings = deformationSettings;
            Console.WriteLine($"{BenchmarkRunner.Elapsed} Applied deformation settings at frame {frame}: {deformationSettings.Mode}, batch size {DeformationBatchSize}, thread group {deformationSettings.ThreadGroup}.");
        }
    }

    private MeshDeformationSettings deformationSettings;

    // Mirrors ModelRenderProcessor's per-instance choice for the active mode.
    private bool ComputeSkinning(ModelComponent model)
        => deformationSettings != null && (deformationSettings.Mode == MeshDeformationMode.Compute || deformationSettings.Mode == MeshDeformationMode.Auto && model.IsShadowCaster);

    private void FinishScenario()
    {
        SaveResult(workloads[caseIndex]);
        gpuTimers.ResetCase(caseIndex);
        waitingForGpu = false;
        updateMs.Clear(); drawMs.Clear();
        gpuVerified = false;
        Array.Fill(previous, -1);
        frame = -1;
        if (++caseIndex == workloads.Length)
        {
            if (preview) caseIndex = 0;
            else Exit();
        }
    }

    private void SaveResult(MorphWorkload workload)
    {
        object Stats(List<double> values)
        {
            var sorted = values.Order().ToArray();
            return new { samples = sorted.Length, meanMs = sorted.Average(), medianMs = sorted[sorted.Length / 2], p95Ms = sorted[(int)((sorted.Length - 1) * 0.95)] };
        }
        Directory.CreateDirectory(output);
        string path = Path.Combine(output, $"{mode}-{(animateSkinning ? "skinned" : "morph")}-{workload.Name}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new {
            scenario = workload.Name, instanceCount = InstanceCount, sharedModels = MeshCount, instancesPerMesh = InstancesPerMesh,
            mesh = "procedural open cylinder", vertexCount = sharedModel.Meshes.Sum(mesh => mesh.Draw.VertexBuffers[0].Count), targetCount = MorphWorkload.TargetCount,
            evaluationMode = mode, skinning = animateSkinning ? (VertexShaderSkinning ? "animated-vertex-shader" : "animated-fused") : "disabled-bind-pose", gpuVerified, skinningOnly = SkinningOnly,
            batchSize = DeformationBatchSize, threadGroup = DeformationThreadGroup,
            frustumCulling, visibleEntities = benchmarkCamera.VisibleEntities,
            animation = "fixed-frame bone Z rotations, amplitude 0.18 radians; identical across instances", shadows = PointShadows || DirectionalShadows, directionalShadows = DirectionalShadows, pointLights = PointLights ? 4 : 0, pointLightRadius = PointLights ? 12f : 0f, zeroMorphs = ZeroMorphs, shadowViews = timedShadows.ViewCount, shadowMeshSubmissions = timedShadows.MeshSubmissions, postEffects = false, vsync = false,
            warmupFrames = workload.WarmupFrames, measuredFrames = workload.MeasuredFrames,
            cpuUpdate = Stats(updateMs), cpuDrawSubmission = Stats(drawMs),
            gpu = gpuTimers.Report(caseIndex),
            timingNote = "GPU timestamp intervals; stage values sum all configured entities. Deformation includes uploads/state/barriers; Scene excludes present, correctness readback and screenshot. CPU draw submission is measured separately.",
            engineVersion = typeof(Game).Assembly.FullName, adapter = GraphicsDevice.Adapter.Description,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{BenchmarkRunner.Elapsed} Completed {workload.Name}: {path}");
        // Capture outside the measured interval; readback waits for the GPU.
        using var screenshot = File.Create(Path.ChangeExtension(path, ".png"));
        GraphicsDevice.Presenter.BackBuffer.Save(GraphicsContext.CommandList, screenshot, ImageFileType.Png);
    }

    protected override void Destroy()
    {
        gpuTimers?.Dispose();
        base.Destroy();
        foreach (var buffer in buffers) buffer.Dispose();
    }
}
