using Hexa.NET.ImGui;
using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class ImGuiRenderer
{
    private readonly Dictionary<ulong, Texture> textures = [];
    private readonly HashSet<ulong> ownedTextures = [];
    private ulong nextTextureId = 1;
    private byte[] atlasUpdatePixels = [];

    // External textures remain owned by their caller (for example the scene viewport).
    public ImTextureRef RegisterTexture(Texture texture)
    {
        ObjectDisposedException.ThrowIf(retiring, this);
        ArgumentNullException.ThrowIfNull(texture);
        if (!ReferenceEquals(texture.GraphicsDevice, device))
            throw new InvalidOperationException("The ImGui texture belongs to another graphics device.");
        var id = nextTextureId++;
        textures.Add(id, texture);
        return new ImTextureRef(null, new ImTextureID(id));
    }

    public void UnregisterTexture(ImTextureRef reference)
    {
        var id = reference.GetTexID().Handle;
        if (ownedTextures.Contains(id))
            throw new InvalidOperationException("An owned ImGui atlas must be released through its texture lifecycle.");
        textures.Remove(id);
    }

    // Keep the live ImGui context/docking state and its CPU atlas pixels, but detach
    // every native texture before this renderer's device is replaced. External image
    // owners must subsequently register their recreated textures on the new renderer.
    public void ReleaseDeviceTextures(ImDrawDataPtr data)
    {
        // The atlas inventory exists even if the failed frame never reached Render().
        var inventory = Hexa.NET.ImGui.ImGui.GetPlatformIO().Textures;
        {
            for (var index = 0; index < inventory.Size; index++)
            {
                var texture = inventory[index];
                if (!ownedTextures.Contains(texture.TexID.Handle)) continue;
                texture.SetTexID(ImTextureID.Null);
                texture.SetStatus(texture.Status == ImTextureStatus.WantDestroy
                    ? ImTextureStatus.Destroyed : ImTextureStatus.WantCreate);
            }
        }
        foreach (var id in ownedTextures.ToArray())
        {
            textures[id].Dispose();
            textures.Remove(id);
            ownedTextures.Remove(id);
        }
        textures.Clear();
    }

    private void ProcessTextureRequests(CommandList commandList, ImDrawDataPtr data)
    {
        // Texture requests must be processed even when this frame has no geometry.
        for (var i = 0; i < data.Textures.Size; i++)
        {
            var texture = data.Textures[i];
            if (texture.Status is ImTextureStatus.WantCreate or ImTextureStatus.WantUpdates)
            {
                if (texture.Format != ImTextureFormat.Rgba32)
                    throw new NotSupportedException("The ImGui renderer requires RGBA32 font textures.");
                if (texture.Width <= 0 || texture.Height <= 0 || texture.GetPixels() == null)
                    throw new InvalidDataException("The ImGui atlas has no valid pixel buffer.");
            }
            switch (texture.Status)
            {
                case ImTextureStatus.WantCreate:
                    var creationId = texture.TexID.Handle;
                    Texture gpuTexture;
                    if (creationId != 0)
                    {
                        if (!ownedTextures.Contains(creationId) || !textures.TryGetValue(creationId, out gpuTexture!))
                            throw new InvalidOperationException("The pending ImGui atlas has no current texture owner.");
                    }
                    else
                    {
                        gpuTexture = Texture.New2D(device, texture.Width, texture.Height, PixelFormat.R8G8B8A8_UNorm);
                        var reference = RegisterTexture(gpuTexture);
                        ownedTextures.Add(reference.TexID.Handle);
                        // Preserve the actual owner across a failed upload. The
                        // WantCreate request retries this texture, not a new one.
                        texture.SetTexID(reference.TexID);
                    }
                    if (gpuTexture.Width != texture.Width || gpuTexture.Height != texture.Height)
                        throw new InvalidDataException("The pending ImGui atlas dimensions changed before upload completed.");
                    gpuTexture.SetData(commandList, new ReadOnlySpan<byte>(texture.GetPixels(), checked(texture.Width * texture.Height * 4)));
                    texture.SetStatus(ImTextureStatus.Ok);
                    break;
                case ImTextureStatus.WantUpdates:
                    if (!ownedTextures.Contains(texture.TexID.Handle) || !textures.TryGetValue(texture.TexID.Handle, out var updateTexture))
                        throw new InvalidOperationException("The ImGui atlas update has no current texture owner.");
                    if (updateTexture.Width != texture.Width || updateTexture.Height != texture.Height)
                        throw new InvalidDataException("The ImGui atlas update dimensions do not match its allocation.");
                    if (texture.Updates.Size != 0)
                    {
                        for (var update = 0; update < texture.Updates.Size; update++)
                        {
                            var rectangle = texture.Updates[update];
                            UploadAtlasRectangle(commandList, updateTexture, texture, rectangle.X, rectangle.Y, rectangle.W, rectangle.H);
                        }
                    }
                    else
                    {
                        var rectangle = texture.UpdateRect;
                        if (rectangle.W != 0 && rectangle.H != 0)
                            UploadAtlasRectangle(commandList, updateTexture, texture, rectangle.X, rectangle.Y, rectangle.W, rectangle.H);
                        else
                            updateTexture.SetData(commandList,
                                new ReadOnlySpan<byte>(texture.GetPixels(), checked(texture.Width * texture.Height * 4)));
                    }
                    texture.SetStatus(ImTextureStatus.Ok);
                    break;
                case ImTextureStatus.WantDestroy:
                    var id = texture.TexID.Handle;
                    if (ownedTextures.Contains(id) && textures.TryGetValue(id, out var oldTexture))
                    {
                        oldTexture.Dispose();
                        ownedTextures.Remove(id); textures.Remove(id);
                    }
                    texture.SetTexID(ImTextureID.Null);
                    texture.SetStatus(ImTextureStatus.Destroyed);
                    break;
            }
        }
    }

    private void UploadAtlasRectangle(CommandList commands, Texture target, ImTextureDataPtr source, int x, int y, int width, int height)
    {
        if (width == 0 || height == 0) return;
        if (x < 0 || y < 0 || width < 0 || height < 0 || x > source.Width - width || y > source.Height - height)
            throw new InvalidDataException("The ImGui atlas update rectangle lies outside its pixel buffer.");
        var rowBytes = checked(width * 4);
        var length = checked(rowBytes * height);
        if (atlasUpdatePixels.Length < length) atlasUpdatePixels = new byte[length];
        for (var row = 0; row < height; row++)
            new ReadOnlySpan<byte>(source.GetPixelsAt(x, y + row), rowBytes).CopyTo(atlasUpdatePixels.AsSpan(row * rowBytes, rowBytes));
        target.SetData(commands, (ReadOnlySpan<byte>)atlasUpdatePixels.AsSpan(0, length),
            region: new ResourceRegion(x, y, 0, checked(x + width), checked(y + height), 1));
    }
}
