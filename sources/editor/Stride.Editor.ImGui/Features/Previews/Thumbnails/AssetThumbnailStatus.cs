using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

internal enum AssetThumbnailFailure { None, Broken, MissingSource, Unsupported, AwaitingCompilation }
internal readonly record struct AssetThumbnailStatus(LogMessageType Level, AssetThumbnailFailure Failure)
{
    public static AssetThumbnailStatus Clear => new(LogMessageType.Info, AssetThumbnailFailure.None);
}
internal sealed class AssetSourceUnavailableException(string message, string? path = null) : FileNotFoundException(message, path);
internal sealed class AssetThumbnailNotCompiledException(string url) : FileNotFoundException("The asset has no compiled content.", url);
