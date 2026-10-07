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

/// <summary>
/// Shows one morph-target head with a play/stop button for its clip and a slider per morph target.
/// With <see cref="FbxModel"/> set, a radio group switches between the glTF and FBX imports of the same head at runtime.
/// </summary>
[DataContract("HeadShowcaseController")]
public sealed class HeadShowcaseController : SyncScript
{
    private const string ClipName = "Clip";
    private static readonly Color Animated = new(120, 180, 245);

    public Model Model { get; set; }
    /// <summary>The same head imported from FBX; the glTF clip drives it too, since the targets have the same names.</summary>
    public Model FbxModel { get; set; }
    public AnimationClip Animation { get; set; }
    public Material Material { get; set; }
    public SpriteFont Font { get; set; }

    private ModelComponent head;
    private AnimationComponent animation;
    private Slider[] sliders = Array.Empty<Slider>();
    private TextBlock[] valueLabels = Array.Empty<TextBlock>();
    private TextBlock[] nameLabels = Array.Empty<TextBlock>();
    private TextBlock playLabel;
    private bool updatingSliders;
    // Sliders are bound to target names: FBX and glTF imports order their targets differently.
    private string[] names = Array.Empty<string>();

    private bool Playing => animation?.PlayingAnimations.Count > 0;

    public override void Start()
    {
        if (Model == null)
            throw new InvalidOperationException("Assign Model on the HeadShowcaseController.");
        // Vsync on; the benchmark scenes turn it off.
        GraphicsDevice.Presenter.PresentInterval = PresentInterval.One;

        head = new ModelComponent();
        SetModel(Model);
        var entity = new Entity("Head") { head };
        if (Animation != null)
        {
            animation = new AnimationComponent();
            animation.Animations.Add(ClipName, Animation);
            entity.Add(animation);
        }
        Entity.AddChild(entity);
        CreateUI();
    }

    public override void Update()
    {
        if (!Playing)
            return;
        var playing = animation.PlayingAnimations[0];
        var duration = Animation.Duration.TotalSeconds;
        playLabel.Text = $"Stop  {playing.CurrentTime.TotalSeconds % duration:0.0} / {duration:0.0} s";

        // While the clip plays, the sliders follow the animated weights.
        updatingSliders = true;
        for (int i = 0; i < sliders.Length; i++)
        {
            float weight = Weight(i);
            sliders[i].Value = weight;
            valueLabels[i].Text = Format(weight);
            // Highlight the targets the clip actually moves.
            if (MathF.Abs(weight) > 0.001f)
                nameLabels[i].TextColor = Animated;
        }
        updatingSliders = false;
    }

    private float Weight(int index) => head.FindMorphTarget(names[index]) is var slot and >= 0 ? head.GetMorphWeight(slot) : 0;

    /// <summary>Swaps the head's model, keeping the current weights by target name.</summary>
    private void SetModel(Model model)
    {
        var weights = names.Select((_, i) => Weight(i)).ToArray();
        head.Model = model;
        head.Materials.Clear();
        if (Material != null)
        {
            for (int i = 0; i < Math.Max(1, model.Materials.Count); i++)
                head.Materials[i] = Material;
        }
        for (int i = 0; i < names.Length; i++)
        {
            if (head.FindMorphTarget(names[i]) is var slot and >= 0)
                head.SetMorphWeight(slot, weights[i]);
        }
    }

    private void TogglePlay()
    {
        if (animation == null)
            return;
        if (Playing)
            animation.PlayingAnimations.Clear();
        else
            animation.Play(ClipName);
        playLabel.Text = Playing ? "Stop" : "Play animation";
    }

    private void Reset()
    {
        if (Playing)
            TogglePlay();
        head.SetAllMorphWeights(0);
        updatingSliders = true;
        for (int slot = 0; slot < sliders.Length; slot++)
        {
            sliders[slot].Value = 0;
            valueLabels[slot].Text = Format(0);
        }
        updatingSliders = false;
    }

    private static string Format(float value) => value.ToString("+0.00;-0.00");

    private void CreateUI()
    {
        var ui = new SampleUI(this, Font);
        names = head.MorphTargetNames.ToArray();
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        panel.Children.Add(ui.Heading("GNM head"));
        var summary = animation != null
            ? $"{names.Length} morph targets; while the clip plays, the sliders follow it and the targets it moves turn blue"
            : $"{names.Length} morph targets";
        panel.Children.Add(ui.Text(summary, 14, SampleUI.Muted, new Thickness(0, 0, 0, 4)));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        if (animation != null)
            buttons.Children.Add(ui.Button(playLabel = ui.Label("Play animation"), TogglePlay));
        buttons.Children.Add(ui.Button(ui.Label("Reset"), Reset));
        panel.Children.Add(buttons);
        if (FbxModel != null)
            ui.RadioGroup(panel, "Source", ["glTF (.glb)", "FBX"], 0, index => SetModel(index == 0 ? Model : FbxModel));

        // Two columns of name, slider, value.
        var rows = Math.Max(1, (names.Length + 1) / 2);
        var grid = new UniformGrid { Columns = 2, Rows = rows, Margin = new Thickness(0, 10, 0, 0) };
        sliders = new Slider[names.Length];
        valueLabels = new TextBlock[names.Length];
        nameLabels = new TextBlock[names.Length];
        for (int slot = 0; slot < names.Length; slot++)
        {
            var name = nameLabels[slot] = ui.Text(names[slot], 14, Color.White, Thickness.UniformCuboid(0));
            name.Width = 76;
            var value = valueLabels[slot] = ui.Text(Format(Weight(slot)), 14, Color.White, new Thickness(8, 0, 0, 0));
            value.Width = 48;
            value.TextAlignment = TextAlignment.Right;
            var slider = sliders[slot] = ui.Slider(-3, 3, Weight(slot), 150, bipolar: true);
            slider.Step = 0.01f;
            slider.Margin = Thickness.UniformCuboid(0);
            var index = slot;
            slider.ValueChanged += (_, _) =>
            {
                if (updatingSliders)
                    return;
                if (head.FindMorphTarget(names[index]) is var target and >= 0)
                    head.SetMorphWeight(target, slider.Value);
                value.Text = Format(slider.Value);
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 20, 1) };
            row.Children.Add(name);
            row.Children.Add(slider);
            row.Children.Add(value);
            row.DependencyProperties.Set(GridBase.ColumnPropertyKey, slot / rows);
            row.DependencyProperties.Set(GridBase.RowPropertyKey, slot % rows);
            grid.Children.Add(row);
        }
        panel.Children.Add(grid);
        SampleUI.Show(this, ui.Panel(panel, HorizontalAlignment.Left));
    }
}
