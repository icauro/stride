namespace Stride.Editor.ImGui;

public sealed partial class EditorExtensionRegistration
{
    internal readonly Dictionary<Enum, byte[]> EnumImages = [];
    /// <summary>Registers the actual encoded PNG artwork for a native enum value; bytes are snapshotted.</summary>
    public void RegisterEnumImage(Enum value, byte[] png)
    {
        Check(); ArgumentNullException.ThrowIfNull(value); ArgumentNullException.ThrowIfNull(png);
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (png.Length < signature.Length || !png.AsSpan(0, signature.Length).SequenceEqual(signature))
            throw new ArgumentException("Enum artwork requires an encoded PNG image.", nameof(png));
        EnumImages[value] = (byte[])png.Clone();
    }
    public void RegisterEnumImageFile(Enum value, string path)
    {
        Check(); ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Enum image paths must be absolute.", nameof(path));
        RegisterEnumImage(value, File.ReadAllBytes(path));
    }
}
