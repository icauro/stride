using System.Diagnostics;
using System.Text.Json;
using MorphBenchmark;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Graphics.Data;
using Stride.Graphics.Semantics;
using Stride.Importer.ThreeD;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Stride.Rendering.Lights;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Shaders.Compiler;
using Buffer = Stride.Graphics.Buffer;
#if NALA
using WeightComponent = Stride.Engine.BlendShapeComponent;
#else
using WeightComponent = Stride.Engine.ModelComponent;
#endif

string Value(string option, string fallback)
{
    int index = Array.IndexOf(args, option);
    return index < 0 ? fallback : index + 1 < args.Length ? args[index + 1] : throw new ArgumentException($"Missing {option} value.");
}
string FindBenchmarkWorkspace()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
    {
        foreach (string candidate in new[] { directory.FullName, Path.Combine(directory.FullName, "2026-10-06-compute-skinning/morph-benchmark") })
            if (File.Exists(Path.Combine(candidate, "workloads-16/workloads.json"))) return candidate;
    }
    throw new DirectoryNotFoundException("Cannot locate the sample workload. Supply --workload and --asset paths.");
}
string configuredManifest = Value("--workload", null);
string root = configuredManifest != null
    ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configuredManifest)), ".."))
    : FindBenchmarkWorkspace();
string manifest = Value("--workload", Path.Combine(root, "workloads-16/workloads.json"));
string asset = Value("--asset", Path.Combine(root, "../morph-generator/generated/Icosphere.glb"));
string scenario = Value("--scenario", "both");
if (scenario is not ("both" or "all-changing" or "mixed-200-200-200")) throw new ArgumentException("Unknown scenario.");
using var game = new ComputeSkinningSample(manifest, asset, scenario, Value("--output", Path.Combine(root, "results")), args.Contains("--preview"), Value("--mode", "sparse"), !args.Contains("--morph-only"), args.Contains("--debug-gpu"), args.Contains("--verify-lifecycle"), args.Contains("--frustum-culling"));
game.RenderDocCapture = args.Contains("--renderdoc-capture");
game.SkinningOnly = args.Contains("--skinning-only");
game.VertexShaderSkinning = args.Contains("--vertex-skinning");
game.ZeroMorphs = args.Contains("--zero-morphs");
game.PointShadows = args.Contains("--point-shadows");
game.DirectionalShadows = args.Contains("--directional-shadows");
game.PointLights = game.PointShadows || args.Contains("--point-lights");
#if NALA
if (game.SkinningOnly || game.VertexShaderSkinning) throw new ArgumentException("These diagnostics target Our model deformation path.");
#endif
game.Run();

sealed partial class ComputeSkinningSample : Game
{
    public bool RenderDocCapture { get; set; }
    public bool SkinningOnly { get; set; }
    public bool VertexShaderSkinning { get; set; }
    public bool ZeroMorphs { get; set; }
    public bool PointShadows { get; set; }
    public bool DirectionalShadows { get; set; }
    public bool PointLights { get; set; }
    private TimedShadowMapRenderer timedShadows;
#if NALA
    private const string Implementation = "Nala";
    private const bool DeformationAvailable = true;
#else
    private const string Implementation = "Our";
    private const bool DeformationAvailable = true;
#endif
    private readonly string manifest, asset, output;
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
    private readonly WeightComponent[] components;
    private readonly Action<int, float>[] setters;
    private readonly int[] previous;
    private readonly int InstanceCount;
    private readonly List<Buffer> buffers = [];
    private readonly List<double> updateMs = [], drawMs = [];
    private int caseIndex, frame = -1;
    private Model sharedModel;
    private GpuTimers gpuTimers;
    private bool waitingForGpu;
    private long gpuDrainStarted;
    private readonly bool frustumCulling;
    private BenchmarkCameraRenderer benchmarkCamera;

    public ComputeSkinningSample(string manifest, string asset, string scenario, string output, bool preview, string mode, bool animateSkinning, bool debugGpu, bool verifyLifecycle, bool frustumCulling)
    {
        if (mode is not ("dense" or "sparse")) throw new ArgumentException("Mode must be dense or sparse.");
        this.mode = mode;
        this.animateSkinning = animateSkinning;
        this.verifyLifecycle = verifyLifecycle;
        this.frustumCulling = frustumCulling;
        this.manifest = manifest;
        this.asset = asset;
        this.output = output;
        this.preview = preview;
        MorphWorkload.VerifyAsset(manifest, asset);
        workloads = (scenario == "both" ? new[] { "all-changing", "mixed-200-200-200" } : new[] { scenario })
            .Select(name => new MorphWorkload(manifest, name)).ToArray();
        InstanceCount = workloads[0].InstanceCount;
        models = new ModelComponent[InstanceCount]; entities = new Entity[InstanceCount];
        components = new WeightComponent[InstanceCount]; setters = new Action<int, float>[InstanceCount];
        previous = Enumerable.Repeat(-1, InstanceCount).ToArray();
#if !NALA
        lastOutputs = new Stride.Graphics.Buffer[InstanceCount];
#endif
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
        GraphicsDevice.DeviceInfoQueueMessage += (ref readonly Silk.NET.Direct3D11.Message message, string description) => Console.WriteLine("GPU: " + description);
        // Standalone scene has no compiled asset package; use the matching branch's
        // shader sources copied beside the executable, with the normal effect cache.
        var compiler = new EffectCompiler(Content.FileProvider) { UseFileSystem = true };
        compiler.SourceDirectories.Add(Path.Combine(AppContext.BaseDirectory, "shaders"));
        EffectSystem.Compiler = new EffectCompilerCache(compiler, Content.FileProvider as Stride.Core.IO.DatabaseFileProvider);
        Console.WriteLine($"Loading {asset}; {Implementation}, GPU morph ready: {DeformationAvailable}");
        sharedModel = new MeshConverter(null).Convert(asset, asset, false);
        if (animateSkinning) sharedModel.Skeleton = new MeshConverter(null).ConvertSkeleton(asset, asset);
        if (sharedModel == null || sharedModel.Meshes.Count == 0) throw new InvalidDataException("Mesh import failed.");
#if NALA
        if (mode == "dense")
        {
            long deltaBytes = sharedModel.Meshes.Sum(mesh => (long)mesh.BlendShapes.VertexCount * mesh.BlendShapes.Targets.Length * 12 *
                (mesh.BlendShapes.TangentOffset >= 0 && mesh.BlendShapes.BaseTangents != null ? 3 : 2)) * InstanceCount;
            ulong dedicated = GraphicsDevice.Adapter.DedicatedVideoMemory;
            if ((ulong)deltaBytes > dedicated)
            {
                Directory.CreateDirectory(output);
                foreach (var workload in workloads)
                {
                    string result = Path.Combine(output, $"{Implementation}-{mode}-skinned-{workload.Name}.json");
                    File.WriteAllText(result, JsonSerializer.Serialize(new { implementation = Implementation, evaluationMode = mode,
                        instanceCount = InstanceCount, scenario = workload.Name, status = "over-memory-budget",
                        reason = "Per-instance dense delta buffers exceed dedicated GPU memory; paging is excluded from this GPU comparison.",
                        estimatedDeltaBytes = deltaBytes, dedicatedVideoMemoryBytes = dedicated }, new JsonSerializerOptions { WriteIndented = true }));
                }
                Console.WriteLine($"Not run: Nala dense needs {deltaBytes / 1073741824.0:F2} GiB of delta buffers; dedicated GPU memory {dedicated / 1073741824.0:F2} GiB.");
                Exit(); return Task.CompletedTask;
            }
        }
#endif
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        var uploaded = new Dictionary<Buffer, Buffer>();
        Buffer Upload(Buffer source, int elementSize, BufferFlags flags)
        {
            if (uploaded.TryGetValue(source, out var existing)) return existing;
            var data = source.GetSerializationData() ?? throw new InvalidDataException("Missing imported buffer bytes.");
            var gpu = Buffer.New(GraphicsDevice, data.Content.AsSpan(), elementSize, flags);
            // Nala's processor needs retained CPU bytes when preparing instance vertex buffers.
            gpu.SetSerializationData(data);
            buffers.Add(gpu);
            uploaded.Add(source, gpu);
            return gpu;
        }
        foreach (var mesh in sharedModel.Meshes)
        {
            var binding = mesh.Draw.VertexBuffers[0];
            var positions = new Vector3[binding.Count];
            var reader = new VertexBufferHelper(binding, binding.Buffer.GetSerializationData().Content, out _);
            reader.Copy<PositionSemantic, Vector3>(positions);
            Vector3 meshMin = new(float.MaxValue), meshMax = new(float.MinValue);
            foreach (var position in positions) { meshMin = Vector3.Min(meshMin, position); meshMax = Vector3.Max(meshMax, position); }
            mesh.BoundingBox = new BoundingBox(meshMin, meshMax);
            mesh.BoundingSphere = BoundingSphere.FromBox(mesh.BoundingBox);
            min = Vector3.Min(min, meshMin); max = Vector3.Max(max, meshMax);
#if NALA
            if (mesh.BlendShapes == null) throw new InvalidDataException("Imported mesh has no blend shapes.");
            if (mode == "sparse") mesh.BlendShapes.Cook();
            else mesh.BlendShapes.CookedData = null;
            // Compute deformation consumes its serialized arrays. Legacy per-target
            // vertex streams are not used by this benchmark's compute-render path.
            mesh.Draw.VertexBuffers = [mesh.Draw.VertexBuffers[0]];
#else
            if (mesh.MorphTargets != null) mesh.MorphTargets = mesh.MorphTargets.WithLayout(mode == "dense" ? MeshMorphLayout.DenseMorphMajor : MeshMorphLayout.SparseVertexMajor);
            mesh.MorphTargets?.Validate();
            if (mesh.MorphTargets == null) throw new InvalidDataException("Imported mesh has no morph targets.");
#endif
            if (!animateSkinning) { mesh.Skinning = null; mesh.NodeIndex = 0; }
            for (int index = 0; index < mesh.Draw.VertexBuffers.Length; index++)
            {
                var vb = mesh.Draw.VertexBuffers[index];
                mesh.Draw.VertexBuffers[index] = new VertexBufferBinding(Upload(vb.Buffer, vb.Stride, BufferFlags.VertexBuffer), vb.Declaration, vb.Count, vb.Stride, vb.Offset);
            }
            var ib = mesh.Draw.IndexBuffer;
            if (ib != null) mesh.Draw.IndexBuffer = new IndexBufferBinding(Upload(ib.Buffer, ib.Is32Bit ? 4 : 2, BufferFlags.IndexBuffer), ib.Is32Bit, ib.Count, ib.Offset);
            mesh.MaterialIndex = 0;
        }
        if (!animateSkinning) sharedModel.Skeleton = null;
        animatedNodes = sharedModel.Meshes.SelectMany(mesh => mesh.Skinning?.Bones.Select(bone => bone.NodeIndex) ?? Enumerable.Empty<int>()).Where(node => node != 0).Distinct().ToArray();
        if (animateSkinning && animatedNodes.Length == 0) throw new InvalidDataException("The benchmark mesh has no imported skeleton/bones.");
        bindRotations = animatedNodes.Select(node => sharedModel.Skeleton.Nodes[node].Transform.Rotation).ToArray();
        sharedModel.BoundingBox = new BoundingBox(min, max);
        sharedModel.BoundingSphere = BoundingSphere.FromBox(sharedModel.BoundingBox);
        sharedModel.Materials.Clear();
        sharedModel.Materials.Add(Material.New(GraphicsDevice, new MaterialDescriptor {
            Attributes = { Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(new Color4(0.3f, 0.65f, 0.9f, 1))), DiffuseModel = new MaterialDiffuseLambertModelFeature() } }));

        var scene = SceneSystem.SceneInstance.RootScene;
        Vector3 center = (min + max) * 0.5f;
        float extent = Math.Max((max - min).Length(), 1);
        int gridSize = (int)Math.Ceiling(Math.Sqrt(InstanceCount));
        for (int instance = 0; instance < InstanceCount; instance++)
        {
            var modelComponent = new ModelComponent(sharedModel) { IsShadowCaster = PointShadows || DirectionalShadows };
#if !NALA
            modelComponent.SkinningMode = VertexShaderSkinning ? SkinningMode.VertexShader : SkinningMode.Compute;
#endif
            models[instance] = modelComponent;
#if NALA
            var morph = new WeightComponent();
            morph.UseFusedSkinning = false;
#else
            var morph = modelComponent;
#endif
            components[instance] = morph;
            var entity = new Entity($"Morph benchmark {instance}") { modelComponent };
#if NALA
            entity.Add(morph);
#endif
            entities[instance] = entity;
            entity.Transform.Position = -center + new Vector3((instance % gridSize - (gridSize - 1) * 0.5f) * extent * 1.5f, (instance / gridSize - (gridSize - 1) * 0.5f) * extent * 1.5f, 0);
            scene.Entities.Add(entity);
#if NALA
            morph.InitializeFromModel(modelComponent);
            workloads[0].MapTargets(morph.TargetNames);
            setters[instance] = (target, weight) => morph.SetWeight(workloads[caseIndex].TargetNames[target], weight);
#else
            var mappings = sharedModel.Meshes.Select(mesh => workloads[0].MapTargets(mesh.MorphTargets.TargetNames)).ToArray();
            setters[instance] = (target, weight) => {
                for (int mesh = 0; mesh < mappings.Length; mesh++) morph.SetMorphWeight(mesh, mappings[mesh][target], weight);
            };
#endif
            if (!ReferenceEquals(modelComponent.Model, sharedModel)) throw new InvalidOperationException("Model is not shared.");
        }
#if !NALA
        if (ZeroMorphs)
            for (int instance = 0; instance < InstanceCount; instance++)
            {
                models[instance].SetAllMorphWeights(0f);
                models[instance].Morphs.Enabled = false;
                setters[instance] = (_, _) => { };
            }
        if (SkinningOnly)
        {
            foreach (var mesh in sharedModel.Meshes) mesh.MorphTargets = null;
            for (int instance = 0; instance < InstanceCount; instance++)
            {
                models[instance].Morphs.Enabled = false;
                setters[instance] = (_, _) => { };
            }
        }
#endif
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
        gpuTimers = new GpuTimers(GraphicsDevice, mode);
        var lighting = SceneSystem.GraphicsCompositor.RenderSystem.RenderFeatures.OfType<MeshRenderFeature>().Single().RenderFeatures.OfType<ForwardLightingRenderFeature>().Single();
        timedShadows = new TimedShadowMapRenderer(lighting.ShadowMapRenderer, gpuTimers);
        lighting.ShadowMapRenderer = timedShadows;
        Services.AddService<IGpuTimestampRecorder>(gpuTimers);
#if !NALA
        const int deformationOrder = 0;
#else
        const int deformationOrder = -50;
#endif
        SceneSystem.SceneInstance.Processors.Add(new GpuTimingBoundaryProcessor(gpuTimers, true, deformationOrder - 1));
        SceneSystem.SceneInstance.Processors.Add(new GpuTimingBoundaryProcessor(gpuTimers, false, deformationOrder + 1));
        Console.WriteLine($"Scene ready: {InstanceCount} entities, shared model, {sharedModel.Meshes.Sum(mesh => mesh.Draw.VertexBuffers[0].Count)} imported vertices, 600 targets each.");
        return Task.CompletedTask;
    }

    protected override void Update(GameTime gameTime)
    {
        if (sharedModel == null) { base.Update(gameTime); return; }
        if (waitingForGpu) { base.Update(gameTime); return; }
        var workload = workloads[caseIndex];
        frame++;
#if !NALA
        if (verifyLifecycle) ApplyLifecycleChanges();
#endif
        long start = Stopwatch.GetTimestamp();
        for (int instance = 0; instance < InstanceCount; instance++)
            if (entities[instance].Get<ModelComponent>() == models[instance]) workload.ApplyFrame(frame, instance, ref previous[instance], setters[instance]);
#if !NALA
        if (zeroWeightsThisFrame)
        {
            foreach (var name in models[0].Morphs.Weights.Keys.ToArray()) models[0].Morphs.Weights[name] = 0;
            zeroWeightsThisFrame = false;
        }
#endif
        if (animateSkinning)
            for (int instance = 0; instance < InstanceCount; instance++)
                for (int bone = 0; bone < animatedNodes.Length; bone++)
                    models[instance].Skeleton.NodeTransformations[animatedNodes[bone]].Transform.Rotation = bindRotations[bone] * Quaternion.RotationZ((float)Math.Sin(frame / 60.0 + bone * 0.7) * 0.18f);
        // Apply before engine update so processors see this frame's weights.
        base.Update(gameTime);
        if (frame >= workload.WarmupFrames) updateMs.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        if (frame % 60 == 0) Window.Title = $"{Implementation} {mode} | {workload.Name} | {InstanceCount} entities | {frame + 1}/{workload.FrameCount} | {(animateSkinning ? "fused morph + skinning" : "morph only")}";
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
            Console.WriteLine($"Visible entities: {benchmarkCamera.VisibleEntities}/{InstanceCount}; culled: {string.Join(", ", benchmarkCamera.CulledEntities)}");
            if (!frustumCulling && benchmarkCamera.VisibleEntities != InstanceCount) throw new Exception("Benchmark did not render all configured entities.");
        }
#if !NALA
        if (verifyLifecycle)
        {
            if (frame is 12 or 18 or 21 or 25 or 29 or 32 or 35 or 38 or 41 or 45 or 49 or 57) VerifyGpuOutput();
            if (frame is 47 or 54) VerifyModelOutputReleased();
            if (frame == 60) { Console.WriteLine("PASS model deformation lifecycle: zero weights, VS/compute switch, morph controls disable/re-enable, model disable/remove/re-add and replacement with GPU-only source."); Exit(); }
            return;
        }
        if (frame == Math.Max(workload.WarmupFrames - 1, 0) || frame + 1 == workload.FrameCount)
            VerifyGpuOutput(); // Readback is excluded from the timing interval.
#else
        if (frame == Math.Max(workload.WarmupFrames - 1, 0) || frame + 1 == workload.FrameCount)
            VerifyNalaGpuOutput();
#endif
        if (frame + 1 < workload.FrameCount) return;
        waitingForGpu = true;
        gpuDrainStarted = Stopwatch.GetTimestamp();
        gpuTimers.Poll(drain: true);
        if (!gpuTimers.HasPending(caseIndex)) FinishScenario();
    }

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
        string path = Path.Combine(output, $"{Implementation}-{mode}-{(animateSkinning ? "skinned" : "morph")}-{workload.Name}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new {
            implementation = Implementation, scenario = workload.Name, instanceCount = InstanceCount, sharedModel = true,
            deformationAvailable = DeformationAvailable, comparableMorphPerformance = DeformationAvailable,
            evaluationMode = mode, skinning = animateSkinning ? (Implementation == "Nala" || VertexShaderSkinning ? "animated-vertex-shader" : "animated-fused") : "disabled-bind-pose", gpuVerified, skinningOnly = SkinningOnly,
            frustumCulling, visibleEntities = benchmarkCamera.VisibleEntities,
            animation = "fixed-frame bone Z rotations, amplitude 0.18 radians; identical across instances", shadows = PointShadows || DirectionalShadows, directionalShadows = DirectionalShadows, pointLights = PointLights ? 4 : 0, pointLightRadius = PointLights ? 12f : 0f, zeroMorphs = ZeroMorphs, shadowViews = timedShadows.ViewCount, shadowMeshSubmissions = timedShadows.MeshSubmissions, postEffects = false, vsync = false,
            warmupFrames = workload.WarmupFrames, measuredFrames = workload.MeasuredFrames,
            cpuUpdate = Stats(updateMs), cpuDrawSubmission = Stats(drawMs),
            gpu = gpuTimers.Report(caseIndex),
            timingNote = "GPU timestamp intervals; stage values sum all configured entities. Deformation includes uploads/state/barriers; Scene excludes present, correctness readback and screenshot. CPU draw submission is measured separately.",
            engineVersion = typeof(Game).Assembly.FullName, adapter = GraphicsDevice.Adapter.Description,
            manifest = Path.GetFullPath(manifest), asset = Path.GetFullPath(asset),
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Completed {workload.Name}: {path}");
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
