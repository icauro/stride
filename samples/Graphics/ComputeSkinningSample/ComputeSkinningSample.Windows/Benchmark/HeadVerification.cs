using System.Runtime.InteropServices;
using System.Text.Json;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics.Data;
using Stride.Rendering;

/// <summary>
/// --verify-head &lt;folder&gt;: plays the head showcase clip, holds it at 1 s and 2 s, reads back the compute-morphed
/// vertices and compares them with Blender's evaluation of the same frames (GNM_Head_at_1s.glb and GNM_Head_at_2s.glb
/// from sample-3d/gnm/export_gnm_frames.py, next to GNM_Head.glb). Checks the glTF head, then the FBX head the scene's
/// source switch offers. Exits with code 0 on success and 1 on failure.
/// </summary>
static class HeadVerification
{
    private static readonly int[] Seconds = [1, 2];

    public static void Start(Game game, string folder)
    {
        game.Script.AddTask(async () =>
        {
            try
            {
                await Verify(game, folder);
                Environment.ExitCode = 0;
            }
            catch (Exception e)
            {
                Console.WriteLine($"FAIL {e.Message}");
                Environment.ExitCode = 1;
            }
            game.Exit();
        });
    }

    private static async Task Verify(Game game, string folder)
    {
        // The scene's controller creates the head in its Start.
        ModelComponent head = null;
        for (int frame = 0; head == null; frame++)
        {
            if (frame > 600)
                throw new Exception("The head showcase scene did not create its head.");
            await game.Script.NextFrame();
            head = game.SceneSystem.SceneInstance?.RootScene?.Entities.SelectMany(Descendants)
                .Select(entity => entity.Get<ModelComponent>()).FirstOrDefault(model => model?.Entity.Get<AnimationComponent>() != null);
        }
        var animation = head.Entity.Get<AnimationComponent>();
        var controller = game.SceneSystem.SceneInstance.RootScene.Entities.SelectMany(Descendants)
            .Select(entity => entity.Get<ComputeSkinningSample.HeadShowcaseController>()).First(c => c != null);
        foreach (var (format, model) in new[] { ("glTF", controller.Model), ("FBX", controller.FbxModel) })
        {
            if (model == null)
                continue;
            head.Model = model;
            await VerifyModel(game, folder, format, head, animation);
        }
    }

    private static async Task VerifyModel(Game game, string folder, string format, ModelComponent head, AnimationComponent animation)
    {
        var mesh = head.Model.Meshes.Single();
        var data = mesh.MorphTargets;

        // Map every runtime vertex to a glTF vertex with the same base position.
        var basePositions = ReadPositions(Path.Combine(folder, "GNM_Head.glb"));
        // FBX stores doubles, so positions are matched to the nearest glTF vertex within 0.05 mm rather than bit for bit.
        const float cell = 1e-3f, match = 5e-5f;
        static Int3 Cell(Vector3 position) => new((int)MathF.Floor(position.X / cell), (int)MathF.Floor(position.Y / cell), (int)MathF.Floor(position.Z / cell));
        var grid = new Dictionary<Int3, List<int>>();
        for (int index = 0; index < basePositions.Length; index++)
        {
            var key = Cell(basePositions[index]);
            if (!grid.TryGetValue(key, out var list))
                grid[key] = list = new List<int>();
            list.Add(index);
        }
        int Nearest(Vector3 position)
        {
            var center = Cell(position);
            int best = -1;
            float bestDistance = match;
            for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                    for (int z = -1; z <= 1; z++)
                        if (grid.TryGetValue(center + new Int3(x, y, z), out var list))
                            foreach (int index in list)
                                if (Vector3.Distance(basePositions[index], position) is var distance && distance <= bestDistance)
                                    (best, bestDistance) = (index, distance);
            return best;
        }
        var source = mesh.Draw.VertexBuffers[0];
        var sourceBytes = source.Buffer.GetSerializationData()?.Content ?? source.Buffer.GetData<byte>(game.GraphicsContext.CommandList);
        int positionOffset = source.Declaration.EnumerateWithOffsets().First(element => element.VertexElement.SemanticName == "POSITION").Offset;
        var gltfVertex = new int[source.Count];
        for (int vertex = 0; vertex < source.Count; vertex++)
        {
            var position = MemoryMarshal.Read<Vector3>(sourceBytes.AsSpan(source.Offset + vertex * source.Stride + positionOffset));
            if ((gltfVertex[vertex] = Nearest(position)) < 0)
                throw new Exception($"Runtime vertex {vertex} at {position} has no glTF vertex at the same base position; the asset is transformed differently from the GLB.");
        }

        foreach (int seconds in Seconds)
        {
            var expected = ReadPositions(Path.Combine(folder, $"GNM_Head_at_{seconds}s.glb"));
            // Hold the clip at the exact time; the animation processor evaluates it every frame.
            animation.PlayingAnimations.Clear();
            var playing = animation.Play("Clip");
            playing.CurrentTime = TimeSpan.FromSeconds(seconds);
            playing.TimeFactor = 0;
            for (int frame = 0; frame < 4; frame++)
                await game.Script.NextFrame();

            var processor = game.SceneSystem.SceneInstance.Processors.OfType<ModelRenderProcessor>().First(p => p.RenderModels.ContainsKey(head));
            var output = processor.RenderModels[head].Meshes[0].Mesh.Draw.VertexBuffers[0];
            if (ReferenceEquals(output.Buffer, source.Buffer))
                throw new Exception("The head is not morphed by compute; its render mesh still uses the source vertex buffer.");
            var outputBytes = output.Buffer.GetData<byte>(game.GraphicsContext.CommandList);

            // Same bound as the importer test: half-float deltas, up to |weight * delta| * 2^-11 each.
            var weights = Enumerable.Range(0, head.MorphTargetNames.Count).Select(slot => head.GetMorphWeight(slot)).ToArray();
            var tolerance = new float[source.Count];
            foreach (var entry in data.Entries)
            {
                var delta = entry.PositionDelta;
                tolerance[entry.VertexIndex] += MathF.Abs(weights[entry.ShapeIndex]) * (MathF.Abs(delta.X) + MathF.Abs(delta.Y) + MathF.Abs(delta.Z)) / 2048;
            }
            int worst = -1;
            float worstError = 0, worstExcess = float.NegativeInfinity;
            for (int vertex = 0; vertex < source.Count; vertex++)
            {
                var actual = MemoryMarshal.Read<Vector3>(outputBytes.AsSpan(output.Offset + vertex * output.Stride + positionOffset));
                float error = Vector3.Distance(actual, expected[gltfVertex[vertex]]);
                float excess = error - tolerance[vertex] - 2e-5f;
                if (excess > worstExcess)
                    (worst, worstError, worstExcess) = (vertex, error, excess);
            }
            var active = string.Join(", ", head.MorphTargetNames.Select((name, slot) => (name, weight: weights[slot])).Where(x => x.weight != 0).Select(x => $"{x.name}={x.weight:0.0000}"));
            if (worstExcess > 0)
                throw new Exception($"{format} head at {seconds} s, vertex {worst} is {worstError:E3} from Blender's shape (allowed {tolerance[worst] + 2e-5f:E3}); weights {active}.");
            Console.WriteLine($"PASS {format} head at {seconds} s: {source.Count} GPU-morphed vertices match Blender (largest error {worstError:E3}); weights {active}.");
        }
    }

    private static IEnumerable<Entity> Descendants(Entity entity)
        => entity.Transform.Children.SelectMany(child => Descendants(child.Entity)).Prepend(entity);

    // POSITION of the single mesh primitive in a GLB written by Blender's exporter.
    private static Vector3[] ReadPositions(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int jsonLength = BitConverter.ToInt32(bytes, 12);
        using var json = JsonDocument.Parse(bytes.AsMemory(20, jsonLength));
        int binary = 20 + jsonLength + 8;
        var root = json.RootElement;
        int accessorIndex = root.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes").GetProperty("POSITION").GetInt32();
        var accessor = root.GetProperty("accessors")[accessorIndex];
        var view = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
        int Optional(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.GetInt32() : 0;
        int start = binary + Optional(view, "byteOffset") + Optional(accessor, "byteOffset");
        int stride = Optional(view, "byteStride") is var s && s != 0 ? s : 12;
        return Enumerable.Range(0, accessor.GetProperty("count").GetInt32())
            .Select(index => MemoryMarshal.Read<Vector3>(bytes.AsSpan(start + index * stride))).ToArray();
    }
}
