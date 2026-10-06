using Stride.Animations;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Sprites;
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
    public bool ComputeSkinning { get; set; } = true;
    public int ActiveMorphs { get; set; } = 30;
    public float Spacing { get; set; } = 1.5f;
    public int Columns { get; set; } = 8;

    private readonly List<(Entity Female, Entity Male)> pairs = new();
    private readonly List<ModelComponent> models = new();
    private string[] morphNames = Array.Empty<string>();
    private int appliedPairs = -1;
    private int appliedMorphs = -1;
    private TextBlock pairsLabel, morphsLabel, statsLabel;
    private Button skinningButton;
    private TextBlock skinningLabel;
    private ISpriteProvider buttonSprite, buttonPressedSprite;

    public override void Start()
    {
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
            int row = i / Columns, column = i % Columns;
            var origin = new Vector3((column - (Columns - 1) * 0.5f) * Spacing * 2, 0, -row * Spacing);
            var female = CreateCharacter($"Female {i}", FemaleModel, FemaleAnimations, origin + new Vector3(-Spacing * 0.5f, 0, 0), i * 2);
            var male = CreateCharacter($"Male {i}", MaleModel, MaleAnimations, origin + new Vector3(Spacing * 0.5f, 0, 0), i * 2 + 1);
            pairs.Add((female, male));
        }

        CreateUI();
        ApplySkinning();
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
            foreach (var model in models)
                for (int m = ActiveMorphs; m < morphNames.Length; m++)
                    model.Morphs.SetWeight(morphNames[m], 0);
            appliedMorphs = ActiveMorphs;
            morphsLabel.Text = $"Active morphs: {ActiveMorphs} / {morphNames.Length}";
        }

        float time = (float)Game.UpdateTime.Total.TotalSeconds;
        int activeModels = ActivePairs * 2;
        for (int i = 0; i < activeModels; i++)
        {
            var morphs = models[i].Morphs;
            for (int m = 0; m < ActiveMorphs; m++)
                morphs.SetWeight(morphNames[m], MathF.Sin(time * (0.7f + m * 0.13f) + i * 0.9f + m));
        }

        statsLabel.Text = $"{Game.UpdateTime.FramePerSecond:0} FPS  {Game.DrawTime.TimePerFrame.TotalMilliseconds:0.00} ms";
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

    private void ApplySkinning()
    {
        var mode = ComputeSkinning ? SkinningMode.Compute : SkinningMode.VertexShader;
        foreach (var model in models)
            model.SkinningMode = mode;
        skinningLabel.Text = ComputeSkinning ? "Skinning: Compute" : "Skinning: Vertex shader";
    }

    private void CreateUI()
    {
        var track = SolidSprite(new Color(60, 60, 60), 64, 8);
        var fill = SolidSprite(new Color(70, 140, 220), 64, 8);
        var thumb = SolidSprite(new Color(230, 230, 230), 12, 24);
        buttonSprite = SolidSprite(new Color(55, 55, 55), 16, 16);
        buttonPressedSprite = SolidSprite(new Color(70, 140, 220), 16, 16);

        var panel = new StackPanel { Orientation = Orientation.Vertical };

        statsLabel = Label();
        panel.Children.Add(statsLabel);

        pairsLabel = Label();
        panel.Children.Add(pairsLabel);
        panel.Children.Add(IntegerSlider(0, MaxPairs, ActivePairs, track, fill, thumb, value => ActivePairs = value));

        morphsLabel = Label();
        panel.Children.Add(morphsLabel);
        panel.Children.Add(IntegerSlider(0, morphNames.Length, ActiveMorphs, track, fill, thumb, value => ActiveMorphs = value));

        skinningLabel = Label();
        skinningButton = new Button
        {
            Content = skinningLabel,
            NotPressedImage = buttonSprite,
            MouseOverImage = buttonSprite,
            PressedImage = buttonPressedSprite,
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        skinningButton.Click += (_, _) => { ComputeSkinning = !ComputeSkinning; ApplySkinning(); };
        panel.Children.Add(skinningButton);

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

    private TextBlock Label() => new TextBlock
    {
        Font = Font,
        TextSize = 18,
        TextColor = Color.White,
        Margin = new Thickness(0, 6, 0, 2),
    };

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
        var texture = Texture.New2D(GraphicsDevice, width, height, PixelFormat.R8G8B8A8_UNorm, pixels);
        return new SpriteFromTexture { Texture = texture };
    }
}
