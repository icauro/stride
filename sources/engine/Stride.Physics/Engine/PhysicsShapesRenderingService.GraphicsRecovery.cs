using System.Collections.Generic;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Physics.Engine
{
    public partial class PhysicsShapesRenderingService
    {
        private readonly HashSet<Buffer> graphicsRecoveryReleasedBuffers = new();
        public bool TryReleaseGraphicsResourcesForRecovery()
        {
            // Old runtime scenes/debug entities must be released by the host first.
            var primitives = new List<IDebugPrimitive>(debugMeshCache.Values);
            primitives.AddRange(debugMeshCache2.Values);
            primitives.AddRange(updatableDebugMeshCache.Values);
            primitives.AddRange(updatableDebugMeshes.Values);
            foreach (var primitive in primitives)
                foreach (var draw in primitive.GetMeshDraws())
                {
                    if (draw.VertexBuffers != null)
                        foreach (var binding in draw.VertexBuffers) Release(binding.Buffer);
                    if (draw.IndexBuffer != null) Release(draw.IndexBuffer.Buffer);
                }
            debugMeshCache.Clear(); debugMeshCache2.Clear(); updatableDebugMeshCache.Clear(); updatableDebugMeshes.Clear();
            componentTypeDefaultMaterial.Clear(); componentTypeStaticPlaneMaterial.Clear(); componentTypeHeightfieldMaterial.Clear();
            graphicsDevice = null;
            return true;
        }
        private void Release(Buffer buffer)
        {
            if (buffer == null || graphicsRecoveryReleasedBuffers.Contains(buffer)) return;
            buffer.Dispose(); graphicsRecoveryReleasedBuffers.Add(buffer);
        }
        public void RebindGraphicsResourcesAfterRecovery()
        {
            if (graphicsDevice != null) return;
            Initialize();
            graphicsRecoveryReleasedBuffers.Clear();
        }
    }
}
