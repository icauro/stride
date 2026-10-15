using System.Security.Cryptography;
using Stride.Core.Assets;
using Stride.Core.IO;
using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private sealed record MetadataDiskSnapshot(AssetLoadResult<Asset> Loaded, byte[] Hash);
    private static MetadataDiskSnapshot LoadMetadataDiskSnapshot(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var bytes = File.ReadAllBytes(fullPath);
        using var stream = new MemoryStream(bytes, false);
        return new(AssetFileSerializer.Load<Asset>(stream, new UFile(fullPath)), SHA256.HashData(bytes));
    }
    private AssetMetadataDocument(string path, MetadataDiskSnapshot snapshot, EditorSessionHistory? history, bool readOnly)
        : this(path, snapshot.Loaded, snapshot.Hash, history, readOnly)
    {
        if (GameSettings == null) return;
        loadedGameOrientation = GameSettings.TryGet<RenderingSettings>()?.DisplayOrientation ?? RequiredDisplayOrientation.Default;
        loadedGameOrientationHash = snapshot.Hash.ToArray();
        gameOrientationDiskLoadPending = true;
    }
}

