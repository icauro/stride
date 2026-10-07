using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace ComputeSkinningSample;

[DataContract("ComputeSkinningPlayback")]
public sealed class ComputeSkinningPlayback : SyncScript
{
    public int InstanceIndex { get; set; }
    public string Scenario { get; set; } = "all-changing";
    public bool AnimateMorphs { get; set; } = true;
    public bool AnimateSkinning { get; set; } = true;

    private static readonly Dictionary<string, MorphWorkload> Workloads = new();
    private ModelComponent model;
    private MorphWorkload workload;
    private int[][] mapping;
    private int[] nodes;
    private Quaternion[] rotations;
    private int frame, previous = -1;

    public override void Start()
    {
        model = Entity.Get<ModelComponent>();
        if (!Workloads.TryGetValue(Scenario, out workload)) Workloads.Add(Scenario, workload = new MorphWorkload(Scenario));
        if ((uint)InstanceIndex >= (uint)workload.InstanceCount) throw new InvalidOperationException("InstanceIndex is outside the workload's instance count.");
        mapping = model.Model.Meshes.Select(mesh => workload.MapTargets(mesh.MorphTargets.TargetNames)).ToArray();
        nodes = model.Model.Meshes.SelectMany(mesh => mesh.Skinning?.Bones.Select(bone => bone.NodeIndex) ?? Enumerable.Empty<int>())
            .Where(node => node != 0).Distinct().ToArray();
        rotations = nodes.Select(node => model.Skeleton.NodeTransformations[node].Transform.Rotation).ToArray();
    }

    public override void Update()
    {
        if (AnimateMorphs)
            workload.ApplyFrame(frame, InstanceIndex, ref previous, (target, weight) => {
                for (int mesh = 0; mesh < mapping.Length; mesh++) model.SetMorphWeight(mesh, mapping[mesh][target], weight);
            });
        else previous = -1;
        if (AnimateSkinning)
            for (int bone = 0; bone < nodes.Length; bone++)
                model.Skeleton.NodeTransformations[nodes[bone]].Transform.Rotation = rotations[bone]
                    * Quaternion.RotationZ((float)Math.Sin(frame / 60.0 + bone * 0.7) * 0.18f);
        if (++frame == workload.FrameCount) { frame = 0; previous = -1; }
    }
}
