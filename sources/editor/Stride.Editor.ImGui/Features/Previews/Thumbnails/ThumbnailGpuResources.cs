using Stride.Engine;
using Stride.Graphics;

namespace Stride.Editor.ImGui;

/// <summary>Returned native thumbnail resources, released only after actual submitted GPU completion.</summary>
internal sealed class ThumbnailGpuResources : IDisposable
{
    private readonly EditorGame owner;
    private readonly GraphicsDevice device;
    private readonly List<(IDisposable? Resource, Action Release)> phases = [];
    internal CompiledProjectContent? SourceContent { get; }
    private IDisposable? contentLease;
    internal ThumbnailGpuResources(Game game, CompiledProjectContent? source = null)
    {
        owner = game as EditorGame ?? throw new NotSupportedException("Thumbnail resources require the editor ownership actor.");
        device = game.GraphicsDevice; SourceContent = source; contentLease = source?.AcquireLease();
    }
    internal void Retain(IDisposable resource) => phases.Add((resource, resource.Dispose));
    internal void Retain(Action release) => phases.Add((null, release));
    internal void Transfer(IDisposable resource)
    {
        for (var index = phases.Count - 1; index >= 0; index--)
            if (ReferenceEquals(phases[index].Resource, resource)) phases.RemoveAt(index);
    }
    internal void Retire() => owner.RetirePreviewTargets(this, device);
    public void Dispose()
    {
        while (phases.Count != 0) { phases[^1].Release(); phases.RemoveAt(phases.Count - 1); }
        if (contentLease != null) { contentLease.Dispose(); contentLease = null; }
    }
}
