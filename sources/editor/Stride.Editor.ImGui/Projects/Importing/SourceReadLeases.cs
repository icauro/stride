namespace Stride.Editor.ImGui;

internal sealed class SourceReadLeases(List<FileStream> streams) : IDisposable
{
    public void Dispose()
    {
        lock (streams)
        {
            List<Exception>? failures = null;
            for (var index = streams.Count - 1; index >= 0; index--)
                try { streams[index].Dispose(); streams.RemoveAt(index); }
                catch (Exception error) { (failures ??= []).Add(error); }
            if (failures != null) throw new AggregateException("Factory source read-handle cleanup is incomplete.", failures);
        }
    }
}
