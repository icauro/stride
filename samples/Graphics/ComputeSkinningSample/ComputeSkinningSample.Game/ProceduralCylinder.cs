using System.Runtime.InteropServices;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Graphics.Data;
using Stride.Rendering;
using Buffer = Stride.Graphics.Buffer;

namespace ComputeSkinningSample;

// Seeded benchmark mesh: an open-ended cylinder along Y with a 4-bone chain and
// 600 normal-displacement targets (200 localized, 200 medium, 200 global).
// Vertices are stored ring by ring from the bottom (y = -1) to the top (y = +1).
public static class ProceduralCylinder
{
    public const float Radius = 0.5f, Height = 2;
    private const int BoneCount = 4;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Vertex
    {
        public Vector3 Position, Normal;
        public Vector2 TexCoord;
        public Vector3 Tangent, Bitangent;
        public uint BlendIndices;
        public Vector4 BlendWeights;
    }

    private static readonly (string Name, float MinFraction, float MaxFraction, float MinStrength, float MaxStrength, float Falloff)[] Groups =
        [("Localized", 0.005f, 0.02f, 0.005f, 0.02f, 2f), ("Medium", 0.1f, 0.3f, 0.01f, 0.04f, 1.5f), ("Global", 1f, 1f, 0.02f, 0.08f, 1f)];

    public static Model Create(GraphicsDevice device, MeshMorphLayout layout = MeshMorphLayout.SparseVertexMajor, int segments = 240, int rings = 253, int seed = MorphWorkload.DefaultSeed)
    {
        int columns = segments + 1; // The seam column is duplicated so UVs do not wrap.
        var vertices = new Vertex[rings * columns];
        for (int ring = 0; ring < rings; ring++)
        {
            float v = ring / (float)(rings - 1);
            float y = (v - 0.5f) * Height;
            var (bones, weights) = SkinWeights(v);
            for (int segment = 0; segment < columns; segment++)
            {
                float u = segment / (float)segments;
                float angle = u * 2 * MathF.PI;
                var normal = new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle));
                var tangent = new Vector3(-MathF.Sin(angle), 0, MathF.Cos(angle));
                vertices[ring * columns + segment] = new Vertex
                {
                    Position = new Vector3(normal.X * Radius, y, normal.Z * Radius), Normal = normal, TexCoord = new Vector2(u, 1 - v),
                    Tangent = tangent, Bitangent = Vector3.Cross(normal, tangent), BlendIndices = bones, BlendWeights = weights,
                };
            }
        }
        var indices = new int[(rings - 1) * segments * 6];
        int write = 0;
        for (int ring = 0; ring < rings - 1; ring++)
            for (int segment = 0; segment < segments; segment++)
            {
                int a = ring * columns + segment, b = a + 1, c = a + columns, d = c + 1;
                // Clockwise when viewed from outside, matching imported (winding-flipped) meshes.
                indices[write++] = a; indices[write++] = c; indices[write++] = b;
                indices[write++] = b; indices[write++] = c; indices[write++] = d;
            }

        var declaration = new VertexDeclaration(
            VertexElement.Position<Vector3>(), VertexElement.Normal<Vector3>(), VertexElement.TextureCoordinate<Vector2>(),
            VertexElement.Tangent<Vector3>(), VertexElement.BiTangent<Vector3>(),
            new VertexElement("BLENDINDICES", 0, PixelFormat.R8G8B8A8_UInt), new VertexElement("BLENDWEIGHT", 0, PixelFormat.R32G32B32A32_Float));
        var vertexBuffer = GpuBuffer(device, MemoryMarshal.AsBytes(vertices.AsSpan()).ToArray(), declaration.VertexStride, BufferFlags.VertexBuffer);
        var indexBuffer = GpuBuffer(device, MemoryMarshal.AsBytes(indices.AsSpan()).ToArray(), sizeof(int), BufferFlags.IndexBuffer);

        // Root node plus a chain of bones from the bottom ring to the top.
        var nodes = new ModelNodeDefinition[BoneCount + 1];
        nodes[0] = Node("Armature", -1, Vector3.Zero);
        nodes[1] = Node("Bone0", 0, new Vector3(0, -Height / 2, 0));
        for (int bone = 1; bone < BoneCount; bone++) nodes[bone + 1] = Node($"Bone{bone}", bone, new Vector3(0, Height / BoneCount, 0));
        var world = new Matrix[nodes.Length];
        for (int node = 0; node < nodes.Length; node++)
        {
            var local = Matrix.Translation(nodes[node].Transform.Position);
            world[node] = nodes[node].ParentIndex < 0 ? local : local * world[nodes[node].ParentIndex];
        }

        var mesh = new Mesh
        {
            Name = "Cylinder",
            NodeIndex = 0,
            Draw = new MeshDraw
            {
                PrimitiveType = PrimitiveType.TriangleList,
                DrawCount = indices.Length,
                VertexBuffers = [new VertexBufferBinding(vertexBuffer, declaration, vertices.Length)],
                IndexBuffer = new IndexBufferBinding(indexBuffer, true, indices.Length),
            },
            Skinning = new MeshSkinningDefinition { Bones = Enumerable.Range(1, BoneCount).Select(node => new MeshBoneDefinition { NodeIndex = node, LinkToMeshMatrix = Matrix.Invert(world[node]) }).ToArray() },
            MorphTargets = BuildTargets(vertices, rings, columns, seed, layout),
        };
        var model = new Model { Skeleton = new Skeleton { Nodes = nodes } };
        model.Meshes.Add(mesh);
        return model;
    }

    private static ModelNodeDefinition Node(string name, int parent, Vector3 position)
        => new() { Name = name, ParentIndex = parent, Transform = { Position = position, Rotation = Quaternion.Identity, Scale = Vector3.One }, Flags = ModelNodeFlags.Default };

    private static Buffer GpuBuffer(GraphicsDevice device, byte[] bytes, int elementSize, BufferFlags flags)
    {
        var buffer = Buffer.New(device, bytes.AsSpan(), elementSize, flags);
        // Verification and CPU references read the source bytes back from the buffer.
        buffer.SetSerializationData(new BufferData(flags, bytes));
        return buffer;
    }

    // Linear blend between the two nearest bones along the height.
    private static (uint Bones, Vector4 Weights) SkinWeights(float v)
    {
        float position = Math.Clamp(v * BoneCount - 0.5f, 0, BoneCount - 1);
        int lower = Math.Min((int)position, BoneCount - 2);
        float blend = position - lower;
        var weights = Vector4.Zero;
        weights[lower] = 1 - blend;
        weights[lower + 1] = blend;
        return (0u | 1u << 8 | 2u << 16 | 3u << 24, weights);
    }

    // Each target covers a contiguous band of rings (a contiguous vertex range) and only
    // pushes positions along the base normal; normals and tangents are left unchanged.
    // The final layout is written directly (already ordered, in parallel) instead of sorting through MeshMorphData.Create.
    private static MeshMorphData BuildTargets(Vertex[] vertices, int rings, int columns, int seed, MeshMorphLayout layout)
    {
        const float scale = Height; // Largest bounding-box dimension.
        var names = MorphWorkload.DefaultTargetNames;
        int vertexCount = vertices.Length, targetCount = names.Length;
        // Per ring, the (shape, height) of every target covering it, in shape order.
        var coverage = new List<(ushort Shape, float Height)>[rings];
        for (int ring = 0; ring < rings; ring++) coverage[ring] = new();
        int shape = 0;
        for (int group = 0; group < Groups.Length; group++)
        {
            var (name, minFraction, maxFraction, minStrength, maxStrength, falloffPower) = Groups[group];
            var random = new MorphWorkload.Random32((uint)seed ^ 0x27d4eb2du * (uint)(group + 1));
            for (int index = 0; index < MorphWorkload.TargetGroups[group].Count; index++, shape++)
            {
                if (names[shape] != $"{name}_{index:D4}") throw new InvalidOperationException("Target naming drifted from the workload.");
                float fraction = minFraction + (maxFraction - minFraction) * random.NextFloat();
                int bandRings = Math.Clamp((int)MathF.Round(fraction * rings), 1, rings);
                float strength = (minStrength + (maxStrength - minStrength) * random.NextFloat()) * (random.Next() % 2 == 0 ? 1 : -1) * scale;
                int firstRing = (int)(random.Next() % (uint)(rings - bandRings + 1));
                float middle = (bandRings - 1) * 0.5f;
                for (int band = 0; band < bandRings; band++)
                {
                    // Falloff from the band's centre ring to its edges.
                    float x = middle > 0 ? MathF.Abs(band - middle) / middle : 0;
                    coverage[firstRing + band].Add(((ushort)shape, (0.05f + 0.95f * MathF.Pow(1 - x, falloffPower)) * strength));
                }
            }
        }

        var data = new MeshMorphData { Layout = layout, VertexCount = vertexCount, TargetNames = (string[])names.Clone() };
        if (layout == MeshMorphLayout.DenseMorphMajor)
        {
            var dense = new MeshMorphEntry[(long)targetCount * vertexCount];
            Parallel.For(0, targetCount, target =>
            {
                for (int v = 0; v < vertexCount; v++)
                    dense[(long)target * vertexCount + v] = new MeshMorphEntry { VertexIndex = (uint)v, ShapeAndPositionX = (uint)target };
            });
            Parallel.For(0, rings, ring =>
            {
                foreach (var (target, height) in coverage[ring])
                    for (int v = ring * columns; v < (ring + 1) * columns; v++)
                        dense[(long)target * vertexCount + v] = MeshMorphEntry.Create((uint)v, target, vertices[v].Normal * height, Vector3.Zero, Vector3.Zero);
            });
            data.Entries = dense;
            data.VertexOffsets = Array.Empty<uint>();
            return data;
        }

        // Vertex-major gather: a vertex's contributions are its ring's covering targets, already in shape order.
        var offsets = new uint[vertexCount + 1];
        for (int v = 0; v < vertexCount; v++) offsets[v + 1] = offsets[v] + (uint)coverage[v / columns].Count;
        var entries = new MeshMorphEntry[offsets[vertexCount]];
        Parallel.For(0, rings, ring =>
        {
            var covering = coverage[ring];
            for (int v = ring * columns; v < (ring + 1) * columns; v++)
                for (int k = 0; k < covering.Count; k++)
                    entries[offsets[v] + k] = MeshMorphEntry.Create((uint)v, covering[k].Shape, vertices[v].Normal * covering[k].Height, Vector3.Zero, Vector3.Zero);
        });
        data.Entries = entries;
        data.VertexOffsets = offsets;
        return data;
    }
}