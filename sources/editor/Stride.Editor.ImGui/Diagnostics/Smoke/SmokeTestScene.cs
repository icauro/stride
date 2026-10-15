using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Graphics.GeometricPrimitives;
using Stride.Rendering;

namespace Stride.Editor.ImGui;

/// <summary>Legacy smoke-test fixture only; real scene views use EngineViewport.</summary>
internal sealed class SmokeTestScene : IDisposable
{
    private readonly GraphicsDevice device;
    private readonly ImGuiRenderer renderer;
    private readonly GeometricPrimitive cube;
    private readonly EffectInstance effect;
    private readonly Texture white;
    private Texture? depth;
    public Texture? Target { get; private set; }
    public ImTextureRef TextureRef { get; private set; }
    public System.Numerics.Vector3 Rotation = new(20, 35, 0);
    public SmokeTestScene(GraphicsDevice device, ImGuiRenderer renderer, GraphicsContext context)
    {
        this.device = device; this.renderer = renderer;
        cube = GeometricPrimitive.Cube.New(device);
        cube.PipelineState.State.RasterizerState = RasterizerStates.CullNone;
        effect = new EffectInstance(new Effect(device, SpriteEffect.Bytecode));
        white = Texture.New2D(device, 1, 1, PixelFormat.R8G8B8A8_UNorm);
        white.SetData(context.CommandList, new byte[] { 255, 255, 255, 255 });
        effect.Parameters.Set(TexturingKeys.Texture0, white);
        effect.Parameters.Set(TexturingKeys.Sampler, device.SamplerStates.LinearClamp);
        effect.Parameters.Set(SpriteEffectKeys.Color, new Color4(0.25f, 0.65f, 0.95f, 1));
        effect.UpdateEffect(device);
    }
    public void Resize(int width, int height)
    {
        width = Math.Clamp(width, 1, 4096); height = Math.Clamp(height, 1, 4096);
        if (Target?.Width == width && Target.Height == height) return;
        if (Target != null) renderer.UnregisterTexture(TextureRef);
        Target?.Dispose(); depth?.Dispose();
        Target = Texture.New2D(device, width, height, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
        depth = Texture.New2D(device, width, height, PixelFormat.D24_UNorm_S8_UInt, TextureFlags.DepthStencil);
        TextureRef = renderer.RegisterTexture(Target);
    }
    public void Draw(GraphicsContext context)
    {
        if (Target == null) return;
        var commands = context.CommandList;
        commands.SetRenderTargetAndViewport(depth, Target);
        commands.Clear(Target, new Color4(0.07f, 0.09f, 0.12f, 1));
        commands.Clear(depth!, DepthStencilClearOptions.DepthBuffer | DepthStencilClearOptions.Stencil);
        var radians = Rotation * (MathF.PI / 180);
        var world = Matrix.RotationYawPitchRoll(radians.Y, radians.X, radians.Z);
        var view = Matrix.LookAtRH(new Vector3(0, 0, 4), Vector3.Zero, Vector3.UnitY);
        var projection = Matrix.PerspectiveFovRH(MathF.PI / 4, (float)Target.Width / Target.Height, 0.1f, 100);
        effect.Parameters.Set(SpriteBaseKeys.MatrixTransform, world * view * projection);
        cube.Draw(context, effect);
    }
    public void Dispose()
    {
        if (Target != null) renderer.UnregisterTexture(TextureRef);
        Target?.Dispose(); depth?.Dispose(); cube.Dispose(); effect.Dispose(); effect.Effect.Dispose(); white.Dispose();
    }
}
