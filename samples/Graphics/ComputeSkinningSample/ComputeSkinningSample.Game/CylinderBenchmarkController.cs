using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Core.Serialization;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Sprites;
using Stride.UI;
using Stride.UI.Controls;
using Stride.UI.Panels;

namespace ComputeSkinningSample;

/// <summary>Interactive version of the --benchmark workload: procedural cylinders, each mesh shared by several instances.</summary>
[DataContract("CylinderBenchmarkController")]
public sealed class CylinderBenchmarkController : SyncScript
{
    public SpriteFont Font { get; set; }
    public UrlReference<Scene> SwitchScene { get; set; }
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
    private readonly List<int[]> mappings = new();
    private MorphWorkload workload;
    private int[] previous;
    private int[] animatedNodes;
    private Quaternion[] bindRotations;
    private int frame, appliedInstances = -1;
    private TextBlock statsLabel, instancesLabel, morphLabel;
    private ISpriteProvider buttonSprite, buttonPressedSprite;
    private FrameTimings timings;

    public override void Start()
    {
        MeshCount = Math.Max(1, MeshCount);
        InstancesPerMesh = Math.Max(1, InstancesPerMesh);
        int total = MeshCount * InstancesPerMesh;
        ActiveInstances = Math.Clamp(ActiveInstances, 0, total);
        workload = new MorphWorkload(Scenario, total);
        previous = Enumerable.Repeat(-1, total).ToArray();

        int columns = (int)Math.Ceiling(Math.Sqrt(total));
        for (int meshIndex = 0; meshIndex < MeshCount; meshIndex++)
        {
            var model = ProceduralCylinder.Create(GraphicsDevice, Dense ? MeshMorphLayout.DenseMorphMajor : MeshMorphLayout.SparseVertexMajor, seed: MorphWorkload.DefaultSeed + meshIndex);
            foreach (var mesh in model.Meshes)
            {
                mesh.BoundingBox = new BoundingBox(new Vector3(-ProceduralCylinder.Radius - 0.2f, -ProceduralCylinder.Height / 2 - 0.2f, -ProceduralCylinder.Radius - 0.2f),
                    new Vector3(ProceduralCylinder.Radius + 0.2f, ProceduralCylinder.Height / 2 + 0.2f, ProceduralCylinder.Radius + 0.2f));
                mesh.BoundingSphere = BoundingSphere.FromBox(mesh.BoundingBox);
            }
            model.BoundingBox = model.Meshes[0].BoundingBox;
            model.BoundingSphere = model.Meshes[0].BoundingSphere;
            if (Material != null) model.Materials.Add(Material);
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
                mappings.Add(workload.MapTargets(model.Meshes[0].MorphTargets.TargetNames));
            }
        }
        var skeleton = models[0].Model.Skeleton;
        animatedNodes = models[0].Model.Meshes[0].Skinning.Bones.Select(bone => bone.NodeIndex).ToArray();
        bindRotations = animatedNodes.Select(node => skeleton.Nodes[node].Transform.Rotation).ToArray();

        CreateUI();
        timings = new FrameTimings(this);
        UpdateLabels();
    }

    public override void Update()
    {
        if (appliedInstances != ActiveInstances)
        {
            for (int i = 0; i < entities.Count; i++) entities[i].EnableAll(i < ActiveInstances, true);
            appliedInstances = ActiveInstances;
            instancesLabel.Text = $"Active instances: {ActiveInstances} / {entities.Count} ({MeshCount} shared meshes)";
        }
        for (int i = 0; i < ActiveInstances; i++)
        {
            var model = models[i];
            var mapping = mappings[i];
            if (AnimateMorphs) workload.ApplyFrame(frame, i, ref previous[i], (target, weight) => model.SetMorphWeight(0, mapping[target], weight));
            if (AnimateSkinning && model.Skeleton != null)
                for (int bone = 0; bone < animatedNodes.Length; bone++)
                    model.Skeleton.NodeTransformations[animatedNodes[bone]].Transform.Rotation = bindRotations[bone] * Quaternion.RotationZ((float)Math.Sin(frame / 60.0 + bone * 0.7) * 0.18f);
        }
        if (++frame == workload.FrameCount) { frame = 0; Array.Fill(previous, -1); }
        statsLabel.Text = timings.Report();
    }

    public override void Cancel() => timings?.Dispose();

    private void UpdateLabels()
    {
        if (morphLabel == null) return;
        morphLabel.Text = $"Morphs: {(AnimateMorphs ? "animated" : "frozen")}, {(Dense ? "dense" : "sparse")}, {MorphWorkload.TargetCount} targets";
    }

    private void CreateUI()
    {
        var track = SolidSprite(new Color(60, 60, 60), 64, 8);
        var fill = SolidSprite(new Color(70, 140, 220), 64, 8);
        var thumb = SolidSprite(new Color(230, 230, 230), 12, 24);
        buttonSprite = SolidSprite(new Color(55, 55, 55), 16, 16);
        buttonPressedSprite = SolidSprite(new Color(70, 140, 220), 16, 16);

        var panel = new StackPanel { Orientation = Orientation.Vertical };
        panel.Children.Add(statsLabel = Label());
        panel.Children.Add(instancesLabel = Label());
        panel.Children.Add(IntegerSlider(0, MeshCount * InstancesPerMesh, ActiveInstances, track, fill, thumb, value => ActiveInstances = value));
        panel.Children.Add(ToggleButton(morphLabel = Label(), () => { AnimateMorphs = !AnimateMorphs; UpdateLabels(); }));
        new DeformationControls(this).AddTo(panel, Label, ToggleButton, (minimum, maximum, value, changed) => IntegerSlider(minimum, maximum, value, track, fill, thumb, changed));

        if (SwitchScene != null)
        {
            var switchLabel = Label();
            switchLabel.Text = "Open character scene";
            panel.Children.Add(ToggleButton(switchLabel, () => SceneSwitch.Load(this, SwitchScene)));
        }

        var root = new Border
        {
            Content = panel,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(16, 16, 0, 0),
            Padding = new Thickness(12, 12, 12, 12),
            BackgroundColor = new Color(0, 0, 0, 160),
        };
        Entity.Add(new UIComponent { Page = new UIPage { RootElement = root } });
    }

    private Button ToggleButton(TextBlock label, Action click)
    {
        var button = new Button
        {
            Content = label,
            NotPressedImage = buttonSprite,
            MouseOverImage = buttonSprite,
            PressedImage = buttonPressedSprite,
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        button.Click += (_, _) => click();
        return button;
    }

    private TextBlock Label() => new TextBlock { Font = Font, TextSize = 18, TextColor = Color.White, Margin = new Thickness(0, 6, 0, 2) };

    private static Slider IntegerSlider(int minimum, int maximum, int value, ISpriteProvider track, ISpriteProvider fill, ISpriteProvider thumb, Action<int> changed)
    {
        var slider = new Slider
        {
            Minimum = minimum,
            Maximum = Math.Max(minimum + 1, maximum),
            TickFrequency = Math.Max(1, maximum - minimum),
            ShouldSnapToTicks = true,
            Step = 1,
            Width = 320,
            Height = 24,
            TrackBackgroundImage = track,
            TrackForegroundImage = fill,
            ThumbImage = thumb,
            MouseOverThumbImage = thumb,
        };
        slider.Value = value;
        slider.ValueChanged += (_, _) => changed((int)MathF.Round(slider.Value));
        return slider;
    }

    private ISpriteProvider SolidSprite(Color color, int width, int height)
    {
        var pixels = Enumerable.Repeat(color, width * height).ToArray();
        return new SpriteFromTexture { Texture = Texture.New2D(GraphicsDevice, width, height, PixelFormat.R8G8B8A8_UNorm, pixels) };
    }
}
