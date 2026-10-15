using Hexa.NET.ImGui;
using Stride.Graphics;
using Stride.TextureConverter;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed class ProjectTemplateImageCache(GraphicsDevice device, ImGuiRenderer renderer, Action<IDisposable> retire) : IDisposable
{
    private sealed class ImageEntry(Texture texture, ImGuiRenderer renderer, Vector2 sourceSize) : IDisposable
    {
        internal readonly ImTextureRef Reference = renderer.RegisterTexture(texture);
        internal readonly Vector2 Size = sourceSize;
        internal int LastFrame;
        private bool unregistered, disposed;
        public void Dispose()
        {
            if (disposed) return;
            if (!unregistered) { renderer.UnregisterTexture(Reference); unregistered = true; }
            texture.Dispose(); disposed = true;
        }
    }

    private readonly Dictionary<string, ImageEntry?> images = new(StringComparer.OrdinalIgnoreCase);
    private long version = -1;
    private long nativeVersion = -1;
    private int frame = -1, loadedThisFrame;
    private bool disposing, disposed;

    internal bool TryGet(string path, int currentFrame, out ImTextureRef reference, out Vector2 size)
    {
        ObjectDisposedException.ThrowIf(disposing, this);
        reference = default; size = default;
        if (version != Stride.Assets.Templates.DotNetNewTemplateBridge.ProjectTemplateVersion
            || nativeVersion != Stride.Core.Assets.Templates.TemplateManager.RegistryVersion)
        {
            foreach (var image in images.ToArray())
            {
                if (image.Value != null) retire(image.Value);
                images.Remove(image.Key);
            }
            version = Stride.Assets.Templates.DotNetNewTemplateBridge.ProjectTemplateVersion;
            nativeVersion = Stride.Core.Assets.Templates.TemplateManager.RegistryVersion;
        }
        if (frame != currentFrame) { frame = currentFrame; loadedThisFrame = 0; }
        path = Path.GetFullPath(path);
        if (!images.TryGetValue(path, out var entry))
        {
            if (loadedThisFrame >= 2) return false;
            if (images.Count >= 64)
            {
                var oldest = images.Where(item => item.Value == null || item.Value.LastFrame < currentFrame - 2)
                    .OrderBy(item => item.Value?.LastFrame ?? int.MinValue).FirstOrDefault();
                if (oldest.Key == null) return false;
                if (oldest.Value != null) retire(oldest.Value);
                images.Remove(oldest.Key);
            }
            loadedThisFrame++;
            Texture? texture = null;
            try
            {
                using var stream = File.OpenRead(path);
                using var image = Image.Load(stream);
                using var tool = new TextureTool();
                using var input = tool.Load(image, device.ColorSpace == ColorSpace.Linear);
                tool.Decompress(input, input.Format.IsSRgb);
                var sourceSize = new Vector2(input.Width, input.Height);
                var scale = Math.Min(1d, 192d / Math.Max(input.Width, input.Height));
                if (scale < 1) tool.Resize(input, Math.Max(1, (int)Math.Round(input.Width * scale)), Math.Max(1, (int)Math.Round(input.Height * scale)), Filter.Rescaling.Lanczos3);
                using var pixels = tool.ConvertToStrideImage(input);
                using var png = new MemoryStream(); pixels.Save(png, ImageFileType.Png); png.Position = 0;
                texture = Texture.Load(device, png, loadAsSrgb: device.ColorSpace == ColorSpace.Linear);
                if (device.GraphicsDeviceStatus != GraphicsDeviceStatus.Normal)
                    throw GraphicsDeviceException.FromLostDevice(device, device.GraphicsDeviceStatus);
                entry = new ImageEntry(texture, renderer, sourceSize); texture = null;
            }
            catch (GraphicsDeviceException) { throw; }
            catch (Exception failure) when (device.GraphicsDeviceStatus != GraphicsDeviceStatus.Normal)
            { throw GraphicsDeviceException.FromLostDevice(device, device.GraphicsDeviceStatus, failure); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or TextureToolsException)
            { entry = null; }
            finally { if (texture != null) retire(texture); }
            images[path] = entry;
        }
        if (entry == null) return false;
        entry.LastFrame = currentFrame; reference = entry.Reference; size = entry.Size; return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposing = true;
        foreach (var item in images.ToArray())
        {
            item.Value?.Dispose(); images.Remove(item.Key);
        }
        disposed = true;
    }
}
