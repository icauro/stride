using Stride.Animations;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.UI;
using Stride.UI.Controls;
using Stride.UI.Panels;

namespace ComputeSkinningSample;

[DataContract("CharacterStressController")]
public sealed class CharacterStressController : SyncScript
{
    public Model FemaleModel { get; set; }
    public Model MaleModel { get; set; }
    public List<AnimationClip> FemaleAnimations { get; } = new();
    public List<AnimationClip> MaleAnimations { get; } = new();
    public SpriteFont Font { get; set; }

    public int MaxPairs { get; set; } = 32;
    public int ActivePairs { get; set; } = 4;
    public int ActiveMorphs { get; set; } = 30;
    /// <summary>Drive morph weights from the clips' weight channels instead of the script's sine waves.</summary>
    public bool ClipMorphs { get; set; }
    public float Spacing { get; set; } = 1.5f;
    public int Columns { get; set; } = 8;

    private readonly List<(Entity Female, Entity Male)> pairs = new();
    private readonly List<ModelComponent> models = new();
    private string[] morphNames = Array.Empty<string>();
    // Per model: morph slot of each name in morphNames, or -1 when that model lacks it.
    private int[][] morphSlots = Array.Empty<int[]>();
    private int appliedPairs = -1;
    private int appliedMorphs = -1;
    private TextBlock pairsLabel, morphsLabel;
    private TextBlock[] stats;
    private FrameTimings timings;

    public override void Start()
    {
        // Benchmark scenes present without vsync so frame times are not capped at the refresh rate.
        // Each scene sets its own interval, because during a switch the next scene starts before the previous one stops.
        GraphicsDevice.Presenter.PresentInterval = PresentInterval.Immediate;
        if (FemaleModel == null || MaleModel == null)
            throw new InvalidOperationException("Assign FemaleModel and MaleModel on the CharacterStressController.");

        morphNames = FemaleModel.Meshes.Concat(MaleModel.Meshes)
            .SelectMany(mesh => mesh.MorphTargets?.TargetNames ?? Array.Empty<string>())
            .Distinct(StringComparer.Ordinal).ToArray();
        MaxPairs = Math.Max(0, MaxPairs);
        ActivePairs = Math.Clamp(ActivePairs, 0, MaxPairs);
        ActiveMorphs = Math.Clamp(ActiveMorphs, 0, morphNames.Length);

        for (int i = 0; i < MaxPairs; i++)
        {
            // Fill each row from the middle outwards, so a few pairs stay centred in front of the camera.
            int row = i / Columns, slot = i % Columns;
            int column = Columns / 2 + (slot % 2 == 0 ? slot / 2 : -(slot + 1) / 2);
            var origin = new Vector3((column - (Columns - 1) * 0.5f) * Spacing * 2, 0, -row * Spacing);
            var female = CreateCharacter($"Female {i}", FemaleModel, FemaleAnimations, origin + new Vector3(-Spacing * 0.5f, 0, 0), i * 2);
            var male = CreateCharacter($"Male {i}", MaleModel, MaleAnimations, origin + new Vector3(Spacing * 0.5f, 0, 0), i * 2 + 1);
            pairs.Add((female, male));
        }

        morphSlots = models.Select(model => morphNames.Select(model.FindMorphTarget).ToArray()).ToArray();
        CreateUI();
        timings = new FrameTimings(this);
    }

    public override void Update()
    {
        if (appliedPairs != ActivePairs)
        {
            for (int i = 0; i < pairs.Count; i++)
            {
                bool enabled = i < ActivePairs;
                pairs[i].Female.EnableAll(enabled, true);
                pairs[i].Male.EnableAll(enabled, true);
            }
            appliedPairs = ActivePairs;
            pairsLabel.Text = $"Character pairs: {ActivePairs} ({ActivePairs * 2} models)";
        }

        if (appliedMorphs != ActiveMorphs)
        {
            for (int i = 0; i < models.Count; i++)
                for (int m = ActiveMorphs; m < morphNames.Length; m++)
                    if (morphSlots[i][m] >= 0) models[i].SetMorphWeight(morphSlots[i][m], 0);
            appliedMorphs = ActiveMorphs;
            morphsLabel.Text = $"Active morphs: {ActiveMorphs} / {morphNames.Length}";
        }

        // Clip mode: the animation processor writes weights from the clips' MorphWeights[...] channels.
        if (ClipMorphs)
        {
            timings.Report(stats);
            return;
        }

        float time = (float)Game.UpdateTime.Total.TotalSeconds;
        int activeModels = ActivePairs * 2;
        for (int i = 0; i < activeModels; i++)
        {
            var slots = morphSlots[i];
            var weights = models[i].WriteMorphWeights();
            for (int m = 0; m < ActiveMorphs; m++)
                if (slots[m] >= 0) weights[slots[m]] = MathF.Sin(time * (0.7f + m * 0.13f) + i * 0.9f + m);
        }

        timings.Report(stats);
    }

    private Entity CreateCharacter(string name, Model model, List<AnimationClip> clips, Vector3 position, int index)
    {
        var entity = new Entity(name) { Transform = { Position = position } };
        var modelComponent = new ModelComponent(model);
        entity.Add(modelComponent);
        models.Add(modelComponent);
        if (clips.Count > 0)
        {
            var animation = new AnimationComponent();
            for (int a = 0; a < clips.Count; a++)
                animation.Animations.Add($"Clip{a}", clips[a]);
            entity.Add(animation);
            var playing = animation.Play($"Clip{index / 2 % clips.Count}");
            playing.CurrentTime = TimeSpan.FromSeconds(index * 0.37 % Math.Max(0.001, clips[index / 2 % clips.Count].Duration.TotalSeconds));
        }
        Entity.AddChild(entity);
        return entity;
    }

    public override void Cancel() => timings?.Dispose();

    private void CreateUI()
    {
        var ui = new SampleUI(this, Font);
        var panel = new StackPanel { Orientation = Orientation.Vertical, Width = 340 };
        panel.Children.Add(ui.Heading("Character stress test"));
        stats = ui.Table(panel, FrameTimings.Rows);

        panel.Children.Add(ui.Section("Workload"));
        panel.Children.Add(pairsLabel = ui.Label());
        panel.Children.Add(ui.IntegerSlider(0, MaxPairs, ActivePairs, value => ActivePairs = value));
        panel.Children.Add(morphsLabel = ui.Label());
        panel.Children.Add(ui.IntegerSlider(0, morphNames.Length, ActiveMorphs, value => ActiveMorphs = value));
        ui.RadioGroup(panel, "Morph weights", ["Script sine waves", "Animation clips"], ClipMorphs ? 1 : 0, index => { ClipMorphs = index == 1; appliedMorphs = -1; });

        new DeformationControls(this).AddTo(panel, ui);
        SampleUI.Show(this, ui.Panel(panel, HorizontalAlignment.Left));
    }
}
