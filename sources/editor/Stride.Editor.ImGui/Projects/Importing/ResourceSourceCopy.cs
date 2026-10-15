using System.Security.Cryptography;

namespace Stride.Editor.ImGui;

internal sealed record ResourceSourceCopyChoice(string Source, string Destination, bool Overwrite);
internal sealed record PreparedResourceSourceCopy(string Source, string Destination, byte[] Contents, byte[] Hash,
    byte[]? Previous, byte[]? PreviousHash, FileStream SourceRead);

/// <summary>Reads actual copy inputs without touching either source or destination.</summary>
internal static class ResourceSourceCopy
{
    internal static PreparedResourceSourceCopy[] Prepare(ResourceSourceCopyChoice[] choices, CancellationToken cancellation,
        Action<int, string>? progress = null)
    {
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prepared = new List<PreparedResourceSourceCopy>();
        var held = new List<FileStream>();
        try
        {
        for (var index = 0; index < choices.Length; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            var choice = choices[index]; var source = Path.GetFullPath(choice.Source); var destination = Path.GetFullPath(choice.Destination);
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) continue;
            if (choices.Any(other => string.Equals(Path.GetFullPath(other.Source), destination, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("A resource destination is another source in the import batch.");
            if (!destinations.Add(destination)) throw new IOException("Two source files have the same resource destination.");
            progress?.Invoke(index, source);
            var read = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read); held.Add(read);
            var contents = Read(read, cancellation);
            byte[]? previous = null;
            if (Directory.Exists(destination)) throw new IOException("The resource destination is occupied by a directory.");
            if (File.Exists(destination))
            {
                if (!choice.Overwrite) throw new IOException("The resource file already exists.");
                var old = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read); held.Add(old);
                try { previous = Read(old, cancellation); }
                finally { old.Dispose(); held.Remove(old); }
            }
            prepared.Add(new(source, destination, contents, SHA256.HashData(contents), previous,
                previous == null ? null : SHA256.HashData(previous), read));
        }
        cancellation.ThrowIfCancellationRequested();
        return prepared.ToArray();
        }
        catch (Exception failure)
        {
            var cleanup = new SourceReadLeases(held);
            try { cleanup.Dispose(); }
            catch (Exception disposal) { throw new ResourceSourcePreparationFailure(failure, disposal, cleanup); }
            throw;
        }
    }
    internal static void ReleaseSources(IEnumerable<PreparedResourceSourceCopy> files)
    { new SourceReadLeases(files.Select(file => file.SourceRead).Distinct().ToList()).Dispose(); }
    private static byte[] Read(Stream source, CancellationToken cancellation)
    {
        using var content = new MemoryStream(); var buffer = new byte[65536]; int count;
        while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
        { cancellation.ThrowIfCancellationRequested(); content.Write(buffer, 0, count); }
        cancellation.ThrowIfCancellationRequested(); return content.ToArray();
    }
}


internal sealed class ResourceSourcePreparationFailure(Exception preparation, Exception cleanup, SourceReadLeases sources)
    : AggregateException("Resource source preparation and read-handle cleanup failed.", preparation, cleanup)
{
    internal SourceReadLeases Sources { get; } = sources;
}
