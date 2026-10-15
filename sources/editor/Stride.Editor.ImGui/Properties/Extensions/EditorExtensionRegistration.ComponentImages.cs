using Stride.Engine;

namespace Stride.Editor.ImGui;

public sealed partial class EditorExtensionRegistration
{
    internal readonly Dictionary<Type, byte[]> ComponentImages = [];

    /// <summary>Registers an encoded native PNG image for a real component type. The editor snapshots the bytes.</summary>
    public void RegisterComponentImage(Type componentType, byte[] png)
    {
        Check(); ArgumentNullException.ThrowIfNull(componentType); ArgumentNullException.ThrowIfNull(png);
        if (!typeof(EntityComponent).IsAssignableFrom(componentType))
            throw new ArgumentException("Component images require an EntityComponent type.", nameof(componentType));
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (png.Length < signature.Length || !png.AsSpan(0, signature.Length).SequenceEqual(signature))
            throw new ArgumentException("Component images require an encoded PNG image.", nameof(png));
        ComponentImages[componentType] = (byte[])png.Clone();
    }

    /// <summary>Snapshots an actual provider PNG file; native DrawingImage adapters can export through the supplied icon tooling.</summary>
    public void RegisterComponentImageFile(Type componentType, string path)
    {
        Check(); ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Component image paths must be absolute.", nameof(path));
        RegisterComponentImage(componentType, File.ReadAllBytes(path));
    }
}
