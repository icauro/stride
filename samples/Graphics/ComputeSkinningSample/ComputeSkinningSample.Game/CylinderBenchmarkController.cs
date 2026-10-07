using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.UI;
using Stride.UI.Controls;
using Stride.UI.Panels;

namespace ComputeSkinningSample;

/// <summary>Interactive version of the --benchmark workload: procedural cylinders, each mesh shared by several instances.</summary>
[DataContract("CylinderBenchmarkController")]
public sealed class CylinderBenchmarkController : SyncScript
{
    public SpriteFont Font { get; set; }
    public Material Material { get; set; }

    public int MeshCount { get; set; } = 2;
    public int InstancesPerMesh { get; set; } = 32;
    public int ActiveInstances { get; set; } = 64;
    /// <summary>Morph layout chosen when the meshes are generated.</summary>
    public bool Dense { get; set; } = true;
    public string Scenario { get; set; } = "all-changing";
    public bool AnimateMorphs { get; set; } = true;
    public bool AnimateSkinning { get; set; } = true;
    public float Spacing { get; set; } = 1.5f;

    private readonly List<Entity> entities = new();
    private readonly List<ModelComponent> models = new();
    private readonly List<Model> meshModels = new();
    private int[] mapping;
    private MorphWorkload workload;
    private int[] previous;
    private int[] animatedNodes;
    private Quaternion[] bindRotations;
    private int frame, appliedInstances = -1, appliedMeshes, appliedPerMesh;
    private TextBlock instancesLabel, meshesLabel, perMeshLabel;
    private Slider instancesSlider;
    private TextBlock[] stats;
    private FrameTimings timings;

    public const int MaxInstancesPerMesh = 64;
    // A dense mesh holds 600 x 61k morph entries (about 880 MB on the GPU), so dense allows fewer meshes.
    private int MaxMeshes => Dense ? 4 : 16;
    // Weight streams are precomputed per instance, so instances beyond this reuse them cyclically.
    private const int WorkloadInstances = 64;

    public override void Start()
    {
        // Benchmark scenes present without vsync so frame times are not capped at the refresh rate.
        // Each scene sets its own interval, because during a switch the next scene starts before the previous one stops.
        GraphicsDevice.Presenter.PresentInterval = PresentInterval.Immediate;

        // Generating a 600-target cylinder is the slow part, so it happens once; other meshes are copies of it.
        var template = ProceduralCylinder.Create(GraphicsDevice, Dense ? MeshMorphLayout.DenseMorphMajor : MeshMorphLayout.SparseVertexMajor);
        var mesh = template.Meshes[0];
        mesh.BoundingBox = new BoundingBox(new Vector3(-ProceduralCylinder.Radius - 0.2f, -ProceduralCylinder.Height / 2 - 0.2f, -ProceduralCylinder.Radius - 0.2f),
            new Vector3(ProceduralCylinder.Radius + 0.2f, ProceduralCylinder.Height / 2 + 0.2f, ProceduralCylinder.Radius + 0.2f));
        mesh.BoundingSphere = BoundingSphere.FromBox(mesh.BoundingBox);
        template.BoundingBox = mesh.BoundingBox;
        template.BoundingSphere = mesh.BoundingSphere;
        if (Material != null) template.Materials.Add(Material);
        meshModels.Add(template);

        workload = new MorphWorkload(Scenario, WorkloadInstances);
        mapping = workload.MapTargets(mesh.MorphTargets.TargetNames);
        animatedNodes = mesh.Skinning.Bones.Select(bone => bone.NodeIndex).ToArray();
        bindRotations = animatedNodes.Select(node => template.Skeleton.Nodes[node].Transform.Rotation).ToArray();

        MeshCount = Math.Clamp(MeshCount, 1, MaxMeshes);
        InstancesPerMesh = Math.Clamp(InstancesPerMesh, 1, MaxInstancesPerMesh);
        CreateUI();
        timings = new FrameTimings(this);
        Rebuild();
    }

    /// <summary>
    /// Recreates the instances for the current mesh count. Each extra mesh is a shallow copy of the template:
    /// it reuses the template's buffers and morph data, but the deformation renderer keys shared data by mesh,
    /// so every copy gets its own GPU morph buffers and batches, like a distinct asset.
    /// </summary>
    private void Rebuild()
    {
        foreach (var entity in entities) Entity.RemoveChild(entity);
        entities.Clear();
        models.Clear();

        int total = MeshCount * InstancesPerMesh;
        bool wasFull = ActiveInstances >= appliedMeshes * appliedPerMesh;
        ActiveInstances = wasFull ? total : Math.Clamp(ActiveInstances, 0, total);
        previous = Enumerable.Repeat(-1, total).ToArray();

        int columns = (int)Math.Ceiling(Math.Sqrt(total));
        for (int meshIndex = 0; meshIndex < MeshCount; meshIndex++)
        {
            while (meshModels.Count <= meshIndex) meshModels.Add(Copy(meshModels[0]));
            var model = meshModels[meshIndex];
            for (int i = 0; i < InstancesPerMesh; i++)
            {
                int index = meshIndex * InstancesPerMesh + i;
                var position = new Vector3((index % columns - (columns - 1) * 0.5f) * Spacing, (index / columns) * Spacing * 2, 0);
                var component = new ModelComponent(model);
                var entity = new Entity($"Cylinder {meshIndex}.{i}") { Transform = { Position = position } };
                entity.Add(component);
                Entity.AddChild(entity);
                entities.Add(entity);
                models.Add(component);
            }
        }

        appliedMeshes = MeshCount;
        appliedPerMesh = InstancesPerMesh;
        appliedInstances = -1;
        // Lowering the maximum clamps the slider and raises ValueChanged, so restore the count afterwards.
        int active = ActiveInstances;
        instancesSlider.Maximum = Math.Max(1, total);
        instancesSlider.TickFrequency = Math.Max(1, total);
        instancesSlider.Value = active;
        ActiveInstances = active;
        long morphBytes = (long)meshModels[0].Meshes[0].MorphTargets.Entries.Length * MeshMorphEntry.SizeInBytes * MeshCount;
        meshesLabel.Text = $"Shared meshes: {MeshCount} ({morphBytes / (1024.0 * 1024 * 1024):0.0} GB morph data)";
        perMeshLabel.Text = $"Instances per mesh: {InstancesPerMesh}";
    }

    private static Model Copy(Model source)
    {
        var copy = new Model { Skeleton = source.Skeleton, BoundingBox = source.BoundingBox, BoundingSphere = source.BoundingSphere };
        foreach (var mesh in source.Meshes) copy.Meshes.Add(new Mesh(mesh));
        foreach (var material in source.Materials) copy.Materials.Add(material);
        return copy;
    }

    public override void Update()
    {
        if (appliedMeshes != MeshCount || appliedPerMesh != InstancesPerMesh)
            Rebuild();
        if (appliedInstances != ActiveInstances)
        {
            for (int i = 0; i < entities.Count; i++) entities[i].EnableAll(i < ActiveInstances, true);
            appliedInstances = ActiveInstances;
            instancesLabel.Text = $"Active instances: {ActiveInstances} / {entities.Count}";
        }
        for (int i = 0; i < ActiveInstances; i++)
        {
            var model = models[i];
            if (AnimateMorphs) workload.ApplyFrame(frame, i % WorkloadInstances, ref previous[i], (target, weight) => model.SetMorphWeight(0, mapping[target], weight));
            if (AnimateSkinning && model.Skeleton != null)
                for (int bone = 0; bone < animatedNodes.Length; bone++)
                    model.Skeleton.NodeTransformations[animatedNodes[bone]].Transform.Rotation = bindRotations[bone] * Quaternion.RotationZ((float)Math.Sin(frame / 60.0 + bone * 0.7) * 0.18f);
        }
        if (++frame == workload.FrameCount) { frame = 0; Array.Fill(previous, -1); }
        timings.Report(stats);
    }

    public override void Cancel() => timings?.Dispose();

    private void CreateUI()
    {
        var ui = new SampleUI(this, Font);
        var panel = new StackPanel { Orientation = Orientation.Vertical, Width = 340 };
        panel.Children.Add(ui.Heading("Cylinder benchmark"));
        stats = ui.Table(panel, FrameTimings.Rows);

        panel.Children.Add(ui.Section("Workload"));
        panel.Children.Add(meshesLabel = ui.Label());
        panel.Children.Add(ui.IntegerSlider(1, MaxMeshes, MeshCount, value => MeshCount = Math.Max(1, value)));
        panel.Children.Add(perMeshLabel = ui.Label());
        panel.Children.Add(ui.IntegerSlider(1, MaxInstancesPerMesh, InstancesPerMesh, value => InstancesPerMesh = Math.Max(1, value)));
        panel.Children.Add(instancesLabel = ui.Label());
        panel.Children.Add(instancesSlider = ui.IntegerSlider(0, MeshCount * InstancesPerMesh, ActiveInstances, value => ActiveInstances = value));
        panel.Children.Add(ui.Text($"{(Dense ? "Dense" : "Sparse")} morph layout, {MorphWorkload.TargetCount} targets per mesh", 13, SampleUI.Muted, new Thickness(0, 6, 0, 0)));
        ui.RadioGroup(panel, "Morphs", ["Animated", "Frozen"], AnimateMorphs ? 0 : 1, index => AnimateMorphs = index == 0);
        ui.RadioGroup(panel, "Weight scenario", ["All changing", "Mixed 200/200/200"], Math.Max(0, Array.IndexOf(MorphWorkload.Scenarios, Scenario)), index =>
        {
            Scenario = MorphWorkload.Scenarios[index];
            workload = new MorphWorkload(Scenario, WorkloadInstances);
            frame = 0;
            Array.Fill(previous, -1);
        });

        new DeformationControls(this).AddTo(panel, ui);
        SampleUI.Show(this, ui.Panel(panel, HorizontalAlignment.Left));
    }
}
