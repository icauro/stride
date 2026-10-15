using System.Text.Json;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

internal enum CompilerRunEvent { None, Started, Succeeded, Failed, Cancelled, FailedToStart }
internal sealed record CompilerRunIdentity(Guid Id, string Source, long Sequence)
{
    private static long nextSequence;
    internal static CompilerRunIdentity Start(string source) => new(Guid.NewGuid(), source, Interlocked.Increment(ref nextSequence));
}
internal sealed record NativeAssetDiagnostic(string Id, int Level, string? Module, string Text, string? Exception,
    string? File, int Line, int Column, string? AssetUrl = null, string? ExceptionType = null)
{
    internal AssetId AssetId => new(Guid.Parse(Id));
    internal LogMessageType Severity => Enum.IsDefined(typeof(LogMessageType), Level) ? (LogMessageType)Level : LogMessageType.Error;
    internal static IEnumerable<NativeAssetDiagnostic> Read(string path, bool allowIncompleteTail = false)
    {
        if (!System.IO.File.Exists(path)) yield break;
        using var reader = new StreamReader(path);
        while (reader.ReadLine() is { } line)
        {
            NativeAssetDiagnostic? diagnostic = null;
            try { diagnostic = JsonSerializer.Deserialize<NativeAssetDiagnostic>(line) ?? throw new InvalidDataException("The native compiler emitted an empty asset diagnostic."); }
            catch (JsonException) when (allowIncompleteTail && reader.EndOfStream) { }
            if (diagnostic == null) yield break;
            _ = diagnostic.AssetId;
            yield return diagnostic;
        }
    }
}
