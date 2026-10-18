// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Core.IO;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Rendering.ProceduralModels;
using Stride.Shaders.Compiler;
using Stride.Rendering.Lights;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (GraphicsDevice.Platform != GraphicsPlatform.WebGPU && !args.Contains("--reference-backend")) throw new InvalidOperationException("Build with StrideGraphicsApi=WebGPU, or explicitly select --reference-backend for comparison.");
        using var game = new ValidationGame(args.Contains("--scene") || args.Contains("--shadows") || args.Contains("--lightstreak") || args.Contains("--posteffects"), args.Contains("--shadows"), args.Contains("--lightstreak"), args.Contains("--posteffects"), args.Contains("--tonemap-only"));
        GameContext context = args.Contains("--window")
            ? GameContextFactory.NewGameContextSDL(96, 64, isUserManagingRun: true)
            : new GameContextHeadless(96, 64);
        try
        {
            game.Run(context);
            for (var tick = 0; tick < 12 && !game.IsExiting; tick++) game.Tick();
        }
        catch
        {
            DumpShadersOnFailure(game.GraphicsDevice);
            throw;
        }
        if (game.RenderedFrames < 6) throw new Exception($"Only {game.RenderedFrames} game frames were rendered.");
        game.Exit();
        game.Tick();
        Console.WriteLine($"PASS: Normal Stride Game/GraphicsDeviceManager lifecycle rendered {game.RenderedFrames} verified {(args.Contains("--posteffects") ? "HDR postprocessing" : args.Contains("--lightstreak") ? "LightStreak postprocessing" : args.Contains("--shadows") ? "directional shadow scene" : args.Contains("--scene") ? "forward material/mesh scene" : "fullscreen")} frames ({(args.Contains("--window") ? "SDL HWND" : "headless")}).");
    }

    private static void DumpShadersOnFailure(GraphicsDevice device)
    {
        if (GraphicsDevice.Platform != GraphicsPlatform.WebGPU) return;
        var directory = Environment.GetEnvironmentVariable("STRIDE_WEBGPU_VALIDATION_ARTIFACTS");
        if (device == null || string.IsNullOrEmpty(directory)) return;
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Validation artifact output must be an absolute directory.");
        Directory.CreateDirectory(directory);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var resources = (IEnumerable<GraphicsResourceBase>)typeof(GraphicsDevice).GetField("Resources", flags)!.GetValue(device)!;
        var index = 0;
        foreach (var pipeline in resources.OfType<PipelineState>())
        {
            var description = (PipelineStateDescription?)typeof(PipelineState).GetField("webGpuDescription", flags)!.GetValue(pipeline);
            if (description?.EffectBytecode == null) continue;
            File.WriteAllText(Path.Combine(directory, $"pipeline-{index}-bindings.json"), System.Text.Json.JsonSerializer.Serialize(
                description.EffectBytecode.Reflection.ResourceGroups.Select(group => new { group.Name, Entries = group.Entries.Select(entry => new { entry.KeyInfo.KeyName, entry.SlotStart, entry.SlotCount, entry.Class, entry.Stages }) }),
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            foreach (var stage in description.EffectBytecode.Stages)
                File.WriteAllBytes(Path.Combine(directory, $"pipeline-{index}-{stage.Stage}.spv"), stage.Data);
            index++;
        }
        Console.WriteLine($"Saved {index} pipeline shader sets to {directory} for failure diagnosis.");
    }

    private sealed class ValidationGame : Game
    {
        private readonly bool validatePostEffects;
        private readonly bool toneMapOnly;
        private Stride.Rendering.Images.LuminanceEffect? luminanceValidation;
        private Texture? grayRadiance;
        private readonly bool validateLightStreak;
        private Stride.Rendering.Images.LightStreak? lightStreak;
        private Texture? lightStreakOutput;
        private Texture? white;
        private readonly bool scene;
        private readonly bool shadows;
        private LightDirectional? sun;
        private ModelComponent? shadowCaster;
        private uint[]? shadowedPixels;
        private FileSystemProvider? shaderProvider;
        public int RenderedFrames { get; private set; }

        public ValidationGame(bool scene, bool shadows, bool validateLightStreak = false, bool validatePostEffects = false, bool toneMapOnly = false)
        {
            this.scene = scene;
            this.shadows = shadows;
            this.validateLightStreak = validateLightStreak;
            this.validatePostEffects = validatePostEffects;
            this.toneMapOnly = toneMapOnly;
            AutoLoadDefaultSettings = false;
            MakeWindowVisibleOnRun = false;
            TreatNotFocusedLikeMinimized = false;
            GraphicsDeviceManager.PreferredGraphicsProfile = [GraphicsProfile.Level_11_0];
            GraphicsDeviceManager.PreferredBackBufferWidth = 96;
            GraphicsDeviceManager.PreferredBackBufferHeight = 64;
            GraphicsDeviceManager.PreferredColorSpace = validatePostEffects ? ColorSpace.Linear : ColorSpace.Gamma;
            GraphicsDeviceManager.DeviceCreationFlags = DeviceCreationFlags.None;
            GraphicsDeviceManager.SynchronizeWithVerticalRetrace = false;
        }

        protected override Task LoadContent()
        {
            if (scene)
            {
                shaderProvider = new FileSystemProvider($"/wgpu-game-shaders-{Guid.NewGuid():N}", AppContext.BaseDirectory);
                var compiler = new ValidationCompiler(shaderProvider) { ValidateSpirv = true };
                compiler.SourceDirectories.Add("Shaders");
                EffectSystem.Compiler?.Dispose();
                EffectSystem.Compiler = new EffectCompilerCache(compiler, Content.FileProvider) { CompileEffectAsynchronously = false };
                var camera = new CameraComponent();
                var cameraEntity = new Entity { camera };
                cameraEntity.Transform.Position = new Vector3(0, 0, 3);
                var model = new CubeProceduralModel().Generate(Services);
                model.Materials.Add(Material.New(GraphicsDevice, new MaterialDescriptor
                {
                    Attributes = { Emissive = new MaterialEmissiveMapFeature(new ComputeColor(new Color4(1, 0, 0, 1))) }
                }));
                var root = new Scene();
                root.Entities.Add(cameraEntity);
                root.Entities.Add(new Entity { new ModelComponent { Model = model } });
                if (shadows)
                {
                    camera.FarClipPlane = 20;
                    cameraEntity.Transform.Position = new Vector3(3, 3, 6);
                    cameraEntity.Transform.Rotation = Quaternion.RotationYawPitchRoll(.464f, -.35f, 0);
                    root.Entities[1].Transform.Position = new Vector3(-2, 1, 0);
                    var material = Material.New(GraphicsDevice, new MaterialDescriptor { Attributes =
                    {
                        Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(Color4.White)),
                        DiffuseModel = new MaterialDiffuseLambertModelFeature()
                    } });
                    var cube = new CubeProceduralModel().Generate(Services);
                    cube.Materials.Add(material);
                    shadowCaster = new ModelComponent { Model = cube };
                    var caster = new Entity { shadowCaster };
                    caster.Transform.Position = new Vector3(0, .6f, 0);
                    root.Entities.Add(caster);
                    var plane = new PlaneProceduralModel().Generate(Services);
                    plane.Materials.Add(material);
                    var ground = new Entity { new ModelComponent { Model = plane } };
                    ground.Transform.Scale = new Vector3(12, 1, 12);
                    root.Entities.Add(ground);
                    sun = new LightDirectional();
                    sun.Shadow.Enabled = true;
                    sun.Shadow.Size = LightShadowMapSize.Small;
                    sun.Shadow.CascadeCount = LightShadowMapCascadeCount.OneCascade;
                    sun.Shadow.BiasParameters.DepthBias = .001f;
                    sun.Shadow.BiasParameters.NormalOffsetScale = 0;
                    var light = new Entity { new LightComponent { Type = sun, Intensity = 1 } };
                    light.Transform.Rotation = Quaternion.RotationYawPitchRoll(.7f, -.8f, 0);
                    root.Entities.Add(light);
                }
                SceneSystem.SceneInstance = new SceneInstance(Services, root);
                SceneSystem.GraphicsCompositor = GraphicsCompositorHelper.CreateDefault(enablePostEffects: false, camera: camera, clearColor: new Color4(0, 0, 1, 1), graphicsProfile: GraphicsProfile.Level_11_0);
                if (validatePostEffects)
                {
                    var postEffects = new Stride.Rendering.Images.PostProcessingEffects();
                    postEffects.AmbientOcclusion.Enabled = false;
                    postEffects.LocalReflections.Enabled = false;
                    postEffects.DepthOfField.Enabled = false;
                    postEffects.Bloom.Afterimage.Enabled = false;
                    if (toneMapOnly)
                    {
                        postEffects.DisableAll();
                        postEffects.ColorTransforms.Enabled = true;
                    }
                    postEffects.ColorTransforms.Transforms.Add(new Stride.Rendering.Images.ToneMap
                    {
                        Operator = new Stride.Rendering.Images.ToneMapHejl2Operator()
                    });
                    ((ForwardRenderer)((SceneCameraRenderer)SceneSystem.GraphicsCompositor.Game).Child).PostEffects = postEffects;
                }
                foreach (var feature in SceneSystem.GraphicsCompositor.RenderSystem.RenderFeatures.OfType<RootEffectRenderFeature>())
                    feature.ComputeFallbackEffect = (_, effect, state) => state == RenderEffectState.Error
                        ? throw new InvalidOperationException("Scene shader compilation failed.", effect.PendingEffect?.Exception)
                        : null;
                return Task.CompletedTask;
            }
            white = Texture.New2D(GraphicsDevice, 2, 2, PixelFormat.R8G8B8A8_UNorm, new uint[] { uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue });
            return Task.CompletedTask;
        }

        protected override void RawTickProducer() => RawTick(TimeSpan.FromMilliseconds(16), updateCount: 1, drawFrame: true);

        protected override void Update(GameTime gameTime)
        {
            if (shadowCaster != null) shadowCaster.IsShadowCaster = RenderedFrames < 4;
            base.Update(gameTime);
        }

        protected override void Draw(GameTime gameTime)
        {
            if (scene)
            {
                base.Draw(gameTime);
                if (validateLightStreak)
                {
                    lightStreak ??= new Stride.Rendering.Images.LightStreak();
                    var backBuffer = GraphicsDevice.Presenter.BackBuffer;
                    lightStreakOutput ??= Texture.New2D(GraphicsDevice, backBuffer.Width, backBuffer.Height, backBuffer.Format, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
                    lightStreak.SetInput(0, backBuffer);
                    lightStreak.SetOutput(lightStreakOutput);
                    using var drawContext = new RenderDrawContext(Services, RenderContext.GetShared(Services), GraphicsContext);
                    lightStreak.Draw(drawContext);
                    if (!lightStreakOutput.GetData<uint>(GraphicsContext.CommandList).Any(pixel => (pixel & 0x00ffffff) != 0))
                        throw new Exception("LightStreak produced no color pixels.");
                    RenderedFrames++;
                    if (RenderedFrames >= 8) Exit();
                    return;
                }
                var output = GraphicsDevice.Presenter.BackBuffer;
                var scenePixels = output.GetData<uint>(GraphicsContext.CommandList);
                var center = scenePixels[(output.Height / 2) * output.Width + output.Width / 2];
                var corner = scenePixels[0];
                var bgra = output.Format == PixelFormat.B8G8R8A8_UNorm;
                if (validatePostEffects)
                {
                    grayRadiance ??= Texture.New2D(GraphicsDevice, 96, 64, PixelFormat.R16G16B16A16_Float,
                        Enumerable.Repeat(new Half4(.25f, .25f, .25f, 1), 96 * 64).ToArray());
                    luminanceValidation ??= new Stride.Rendering.Images.LuminanceEffect();
                    luminanceValidation.SetInput(0, grayRadiance);
                    using (var drawContext = new RenderDrawContext(Services, RenderContext.GetShared(Services), GraphicsContext))
                        luminanceValidation.Draw(drawContext);
                    var logLuminance = (float)luminanceValidation.AverageLuminanceTexture.GetData<Stride.Core.Mathematics.Half>(GraphicsContext.CommandList)[0];
                    if (!float.IsFinite(logLuminance) || Math.Abs(logLuminance + 2) > .01f)
                        throw new Exception($"Constant quarter-gray luminance must reduce to log2=-2, got {logLuminance}.");
                    if (RenderedFrames == 7 && Math.Abs(luminanceValidation.AverageLuminance - .25f) > .01f)
                        throw new Exception($"Luminance readback did not converge to quarter-gray: {luminanceValidation.AverageLuminance}.");
                    var red = (center >> (bgra ? 16 : 0)) & 255;
                    var blue = (corner >> (bgra ? 0 : 16)) & 255;
                    var centerBlue = (center >> (bgra ? 0 : 16)) & 255;
                    var cornerRed = (corner >> (bgra ? 16 : 0)) & 255;
                    CaptureFrame(scenePixels, output, "posteffects");
                    if (RenderedFrames >= 3 && (red < centerBlue + 32 || blue < cornerRed + 32))
                        throw new Exception($"HDR postprocessing lost scene color: frame={RenderedFrames}, center={center:x8}, corner={corner:x8}.");
                    if (RenderedFrames == 7) Console.WriteLine($"PASS: Full HDR postprocessing preserves lit colors: center={center:x8}, corner={corner:x8}.");
                }
                else if (shadows)
                {
                    if (RenderedFrames == 3) Console.WriteLine($"Shadow scene state: viewport={GraphicsContext.CommandList.Viewport}, scissor={GraphicsContext.CommandList.Scissor}, targets={GraphicsContext.CommandList.RenderTargetCount}; non-background={scenePixels.Count(pixel => pixel != (bgra ? 0xff0000ffu : 0xffff0000u))}.");
                    if (RenderedFrames == 3) { shadowedPixels = scenePixels; CaptureFrame(scenePixels, output, "caster-on"); }
                    if (RenderedFrames == 7)
                    {
                        var darker = 0;
                        for (var i = 0; i < scenePixels.Length; i++)
                        {
                            var before = shadowedPixels![i]; var after = scenePixels[i];
                            var beforeSum = (before & 255) + ((before >> 8) & 255) + ((before >> 16) & 255);
                            var afterSum = (after & 255) + ((after >> 8) & 255) + ((after >> 16) & 255);
                            if (afterSum > beforeSum + 12) darker++;
                        }
                        CaptureFrame(scenePixels, output, "caster-off");
                        if (darker < 8 || darker > scenePixels.Length / 3) throw new Exception($"Directional shadow scene: disabling the cube's shadow casting brightened {darker} pixels; expected a localized shadow footprint.");
                        Console.WriteLine($"PASS: Disabling only cube shadow casting brightens {darker} scene pixels with directional shadows still enabled.");
                    }
                }
                else if (center != (bgra ? 0xffff0000 : 0xff0000ff) || corner != (bgra ? 0xff0000ff : 0xffff0000))
                    throw new Exception($"Forward scene frame {RenderedFrames}: center={center:x8}, corner={corner:x8}.");
                RenderedFrames++;
                if (RenderedFrames >= 8) Exit();
                return;
            }
            var color = RenderedFrames % 2 == 0 ? new Color4(1, 0, 0, 1) : new Color4(0, 1, 0, 1);
            GraphicsContext.DrawTexture(white!, color);
            var pixels = GraphicsDevice.Presenter.BackBuffer.GetData<uint>(GraphicsContext.CommandList);
            var expected = RenderedFrames % 2 == 0 ? 0xff0000ffu : 0xff00ff00u;
            // The presenter can negotiate BGRA instead of RGBA on a native surface.
            if (GraphicsDevice.Presenter.BackBuffer.Format == PixelFormat.B8G8R8A8_UNorm && RenderedFrames % 2 == 0) expected = 0xffff0000;
            if (pixels.Any(p => p != expected)) throw new Exception($"Game frame {RenderedFrames} pixel mismatch: {pixels[pixels.Length / 2]:x8}.");
            RenderedFrames++;
            if (RenderedFrames >= 8) Exit();
        }

        protected override void Destroy()
        {
            white?.Dispose();
            lightStreak?.Dispose();
            lightStreakOutput?.Dispose();
            grayRadiance?.Dispose();
            luminanceValidation?.Dispose();
            base.Destroy();
            shaderProvider?.Dispose();
        }

        private static void CaptureFrame(uint[] pixels, Texture output, string name)
        {
            var directory = Environment.GetEnvironmentVariable("STRIDE_WEBGPU_VALIDATION_ARTIFACTS");
            if (string.IsNullOrEmpty(directory)) return;
            if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Validation artifact output must be an absolute directory.");
            Directory.CreateDirectory(directory);
            var rgba = new byte[pixels.Length * 4];
            var bgra = output.Format == PixelFormat.B8G8R8A8_UNorm;
            for (var i = 0; i < pixels.Length; i++)
            {
                rgba[i * 4] = (byte)(pixels[i] >> (bgra ? 16 : 0));
                rgba[i * 4 + 1] = (byte)(pixels[i] >> 8);
                rgba[i * 4 + 2] = (byte)(pixels[i] >> (bgra ? 0 : 16));
                rgba[i * 4 + 3] = (byte)(pixels[i] >> 24);
            }
            using var image = SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(rgba, output.Width, output.Height);
            image.SaveAsPng(Path.Combine(directory, $"shadow-{name}.png"));
        }
    }

    private sealed class ValidationCompiler(FileSystemProvider provider) : EffectCompiler(provider)
    {
        public override TaskOrResult<EffectBytecodeCompilerResult> Compile(Stride.Shaders.ShaderMixinSource mixin, EffectCompilerParameters parameters, CompilerParameters compilerParameters, Stride.Core.Storage.ObjectId inputHash)
        {
            EffectBytecodeCompilerResult result;
            try { result = base.Compile(mixin, parameters, compilerParameters, inputHash).WaitForResult(); }
            catch (Exception exception) { Console.Error.WriteLine("SCENE SHADER COMPILATION THREW: " + exception); throw; }
            if (result.CompilationLog.HasErrors)
            {
                var message = string.Join(Environment.NewLine, result.CompilationLog.Messages.Select(message => message.Text));
                Console.Error.WriteLine("SCENE SHADER COMPILATION FAILED: " + message);
                throw new InvalidOperationException(message);
            }
            return result;
        }
    }
}
