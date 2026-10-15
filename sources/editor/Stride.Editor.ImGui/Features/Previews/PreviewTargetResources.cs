using Hexa.NET.ImGui;
using Stride.Engine;
using Stride.Graphics;

namespace Stride.Editor.ImGui;

/// <summary>One real preview target generation, retained until submitted GPU work completes.</summary>
internal sealed class PreviewTargetResources
    : IDisposable
{
    private readonly EditorGame owner;
    private readonly ImGuiRenderer renderer;
    private readonly GraphicsDevice device;
    internal Texture? Target, Depth, Presentation;
    internal ImTextureRef Reference;
    private bool registered;
    internal bool IsDisposed { get; private set; }

    internal PreviewTargetResources(Game game, ImGuiRenderer renderer)
    {
        owner = game as EditorGame ?? throw new NotSupportedException("Preview targets require the editor GPU ownership actor.");
        this.renderer = renderer; device = game.GraphicsDevice;
    }
    internal void Allocate(int width, int height, PixelFormat format, PixelFormat displayFormat, bool depth = false)
    {
        if (!ReferenceEquals(device, owner.GraphicsDevice))
            throw new InvalidOperationException("The preview target device changed before allocation.");
        Target = Texture.New2D(device, width, height, format, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
        if (depth) Depth = Texture.New2D(device, width, height, PixelFormat.D24_UNorm_S8_UInt, TextureFlags.DepthStencil);
        if (format != displayFormat) Presentation = Texture.New2D(device, width, height, displayFormat, TextureFlags.ShaderResource);
        Reference = renderer.RegisterTexture(Presentation ?? Target); registered = true;
    }
    internal void Retire() => owner.RetirePreviewTargets(this, device);
    public void Dispose()
    {
        if (registered) { renderer.UnregisterTexture(Reference); registered = false; }
        if (Depth != null) { Depth.Dispose(); Depth = null; }
        if (Presentation != null) { Presentation.Dispose(); Presentation = null; }
        if (Target != null) { Target.Dispose(); Target = null; }
        IsDisposed = true;
    }
}

internal sealed partial class EditorGame
{
    internal void RetirePreviewTargets(IDisposable resources, GraphicsDevice device)
        => RetireEditorPreview(resources, device);
}
