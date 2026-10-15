using System.Text;

namespace Stride.Editor.ImGui;

/// <summary>Actual compiler lines belonging to one loading operation.</summary>
internal sealed class ProjectLoadProgressCapture
{
    private readonly object gate = new();
    private readonly Queue<string> lines = [];
    private long characters;
    private long revision, renderedRevision = -1;
    private string rendered = "";
    internal void Append(ProjectLoadState state)
    {
        if (state.OutputLine is not { } line) return;
        lock (gate)
        {
            lines.Enqueue(line); characters += line.Length;
            while (lines.Count > 1 && (lines.Count > 10000 || characters > 2 * 1024 * 1024))
                characters -= lines.Dequeue().Length;
            revision++;
        }
    }
    internal string Snapshot()
    {
        string[] snapshot; long capturedRevision; int capacity;
        lock (gate)
        {
            if (renderedRevision == revision) return rendered;
            snapshot = lines.ToArray(); capturedRevision = revision;
            capacity = (int)Math.Min(2 * 1024 * 1024, characters + (long)snapshot.Length * Environment.NewLine.Length);
        }
        // Formatting a large visible log must not hold up compiler pipe readers.
        var text = new StringBuilder(capacity);
        foreach (var line in snapshot) text.AppendLine(line);
        var result = text.ToString();
        lock (gate)
        {
            // A slower reader must not replace a newer snapshot already published by another reader.
            if (capturedRevision > renderedRevision)
            { rendered = result; renderedRevision = capturedRevision; }
            return rendered;
        }
    }
}
