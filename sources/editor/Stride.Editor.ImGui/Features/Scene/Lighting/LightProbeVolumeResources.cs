using Stride.Engine;
using Stride.Graphics;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Editor.ImGui;

/// <summary>Returned native face buffers retained through actual device completion.</summary>
internal sealed class LightProbeVolumeResources(Game game) : IDisposable
{
    private readonly EditorGame owner = game as EditorGame ?? throw new NotSupportedException("Probe volume retirement requires the editor actor.");
    private readonly GraphicsDevice device = game.GraphicsDevice;
    private readonly List<Buffer> buffers = [];
    internal void Retain(Buffer buffer) => buffers.Add(buffer);
    internal void Retire() => owner.RetirePreviewTargets(this, device);
    public void Dispose()
    { while (buffers.Count != 0) { buffers[^1].Dispose(); buffers.RemoveAt(buffers.Count - 1); } }
}
