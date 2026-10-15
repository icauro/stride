using System.Security.Cryptography;
using System.Text.Json;
using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

/// <summary>Persists actual native diagnostic severity bound to certified pixels and their source key.</summary>
internal static class NativeThumbnailCacheEntry
{
    private sealed record Manifest(int Version, string Key, string PngSha256, int Level, int Failure);

    internal static bool TryRead(string directory, string key, out byte[]? png, out AssetThumbnailStatus status)
    {
        png = null; status = AssetThumbnailStatus.Clear;
        var path = EntryPath(directory, key);
        try
        {
            var manifestPath = path + ".status.json";
            if (!File.Exists(path) || !File.Exists(manifestPath) || new FileInfo(path).Length is <= 0 or > 1024 * 1024
                || new FileInfo(manifestPath).Length is <= 0 or > 4096) return false;
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath));
            if (manifest is not { Version: 1 } || manifest.Key != key
                || !Enum.IsDefined(typeof(LogMessageType), manifest.Level)
                || manifest.Failure != (int)AssetThumbnailFailure.None) return false;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length is <= 0 or > 1024 * 1024 || Convert.ToHexString(SHA256.HashData(bytes)) != manifest.PngSha256) return false;
            png = bytes; status = new((LogMessageType)manifest.Level, AssetThumbnailFailure.None); return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    internal static void Store(string directory, string key, byte[] png, AssetThumbnailStatus status, CancellationToken cancellation)
    {
        if (png.Length is <= 0 or > 1024 * 1024 || status.Failure != AssetThumbnailFailure.None)
            throw new InvalidDataException("Only an actual completed bounded thumbnail may enter the native pixel cache.");
        var path = EntryPath(directory, key);
        string? pixelsTemporary = null, statusTemporary = null;
        try
        {
            cancellation.ThrowIfCancellationRequested(); Directory.CreateDirectory(directory);
            var suffix = "." + Guid.NewGuid().ToString("N") + ".tmp";
            pixelsTemporary = path + suffix; statusTemporary = path + ".status.json" + suffix;
            File.WriteAllBytes(pixelsTemporary, png);
            File.WriteAllText(statusTemporary, JsonSerializer.Serialize(new Manifest(1, key,
                Convert.ToHexString(SHA256.HashData(png)), (int)status.Level, (int)status.Failure)));
            cancellation.ThrowIfCancellationRequested();
            // Pixels are the commit point. A concurrent partial/mismatched pair
            // is a cache miss, never invented pixels or diagnostic success.
            File.Move(statusTemporary, path + ".status.json", true); statusTemporary = null;
            cancellation.ThrowIfCancellationRequested(); File.Move(pixelsTemporary, path, true); pixelsTemporary = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } // Cache persistence is optional; retain actual in-memory pixels/status.
        finally
        {
            RemoveTemporary(pixelsTemporary); RemoveTemporary(statusTemporary);
        }
    }

    internal static void RemoveCompanion(string imagePath)
    {
        try { File.Delete(imagePath + ".status.json"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    private static string EntryPath(string directory, string key)
    {
        if (key.Length != 64 || key.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("The native thumbnail cache requires its exact certified SHA256 source key.", nameof(key));
        return Path.Combine(Path.GetFullPath(directory), key + ".png");
    }
    private static void RemoveTemporary(string? path)
    {
        if (path == null) return;
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
