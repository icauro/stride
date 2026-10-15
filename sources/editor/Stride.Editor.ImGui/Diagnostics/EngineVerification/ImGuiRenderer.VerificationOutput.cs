using Hexa.NET.ImGui;
using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class ImGuiRenderer
{
    internal Texture RequireVerificationTexture(ImTextureRef reference)
        => textures.TryGetValue(reference.GetTexID().Handle, out var texture) ? texture
            : throw new InvalidOperationException("The actual preview texture is not registered with the current renderer.");
}

internal sealed partial class EditorGame
{
    internal EngineOutputSample ReadPreviewVerificationOutput(bool model)
    {
        var reference = model ? modelPreview?.TextureRef : texturePreview?.TextureRef;
        if (reference == null || renderer == null) throw new InvalidOperationException("The requested real preview is unavailable.");
        return EngineOutputSample.Read(GraphicsContext.CommandList, renderer.RequireVerificationTexture(reference.Value));
    }
    internal void SavePreviewVerificationOutput(bool model, string path)
    {
        var reference = model ? modelPreview?.TextureRef : texturePreview?.TextureRef;
        if (reference == null || renderer == null) throw new InvalidOperationException("The requested real preview is unavailable.");
        using var file = File.Create(path);
        renderer.RequireVerificationTexture(reference.Value).Save(GraphicsContext.CommandList, file, ImageFileType.Png);
    }
}
