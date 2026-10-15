using Hexa.NET.ImGui;
using Stride.Engine;
using Stride.Graphics;

namespace Stride.Editor.ImGui;

/// <summary>Owns cached thumbnail pixels and their ImGui texture registration.</summary>
internal sealed class AssetThumbnailImage : IDisposable
{
    private readonly ImGuiRenderer renderer;
    private readonly Texture texture;
    private readonly ThumbnailGpuResources resources;
    private bool retired;
    public ImTextureRef Reference { get; }
    private AssetThumbnailImage(Game game, ImGuiRenderer renderer, Texture texture)
    {
        this.renderer = renderer; this.texture = texture;
        resources = new ThumbnailGpuResources(game);
        Reference = renderer.RegisterTexture(texture);
        resources.Retain(texture);
        var reference = Reference;
        resources.Retain(() => renderer.UnregisterTexture(reference));
    }
    internal static AssetThumbnailImage FromTexture(Game game, ImGuiRenderer renderer, Texture texture) => new(game, renderer, texture);
    internal bool Owns(Texture value) => ReferenceEquals(texture, value);
    internal static (AssetThumbnailImage Thumbnail, byte[] Png) Capture(Game game, ImGuiRenderer renderer, Texture target)
    {
        var command = game.GraphicsContext.CommandList;
        using var pixels = target.GetDataAsImage(command);
        if (game.GraphicsDevice.GraphicsDeviceStatus != GraphicsDeviceStatus.Normal)
            throw GraphicsDeviceException.FromLostDevice(game.GraphicsDevice, game.GraphicsDevice.GraphicsDeviceStatus);
        using var encoded = new MemoryStream(); pixels.Save(encoded, ImageFileType.Png);
        var png = encoded.ToArray();
        var displayFormat = game.GraphicsDevice.ColorSpace == ColorSpace.Linear ? PixelFormat.R8G8B8A8_UNorm_SRgb : PixelFormat.R8G8B8A8_UNorm;
        Texture? display = null;
        var failedDisplay = new ThumbnailGpuResources(game);
        try
        {
            if (target.Format != displayFormat)
            {
                display = Texture.New2D(game.GraphicsDevice, 128, 128, displayFormat, TextureFlags.ShaderResource);
                failedDisplay.Retain(display);
                command.ResourceBarrierTransition(target, BarrierLayout.CopySource);
                command.ResourceBarrierTransition(display, BarrierLayout.CopyDest); command.Copy(target, display);
                command.ResourceBarrierTransition(display, BarrierLayout.ShaderResource);
            }
            else command.ResourceBarrierTransition(target, BarrierLayout.ShaderResource);
            var result = FromTexture(game, renderer, display ?? target); display = null;
            return (result, png);
        }
        finally { if (display != null) failedDisplay.Retire(); }
    }
    public static AssetThumbnailImage LoadCached(Game game, ImGuiRenderer renderer, byte[] png)
    {
        using var stream = new MemoryStream(png, false);
        var texture = Texture.Load(game.GraphicsDevice, stream, loadAsSrgb: game.GraphicsDevice.ColorSpace == ColorSpace.Linear);
        try
        {
            if (texture.Width != 128 || texture.Height != 128) throw new InvalidDataException("Invalid cached thumbnail dimensions.");
            return new AssetThumbnailImage(game, renderer, texture);
        }
        catch { var failed = new ThumbnailGpuResources(game); failed.Retain(texture); failed.Retire(); throw; }
    }
    public void Dispose()
    { if (retired) return; resources.Retire(); retired = true; }
}
