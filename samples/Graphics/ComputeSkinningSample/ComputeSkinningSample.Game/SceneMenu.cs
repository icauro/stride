using Stride.Core;
using Stride.Core.Serialization;
using Stride.Engine;
using Stride.Graphics;
using Stride.UI;
using Stride.UI.Controls;
using Stride.UI.Panels;

namespace ComputeSkinningSample;

/// <summary>A scene menu entry: the button label and the scene it opens.</summary>
[DataContract("SceneMenuEntry")]
public sealed class SceneMenuEntry
{
    public string Name { get; set; }
    public UrlReference<Scene> Scene { get; set; }
}

/// <summary>Top-right "Scenes" panel with a button per scene; the current scene's button is highlighted.</summary>
[DataContract("SceneMenu")]
public sealed class SceneMenu : StartupScript
{
    public SpriteFont Font { get; set; }
    public List<SceneMenuEntry> Scenes { get; } = new();

    public override void Start()
    {
        Content.TryGetAssetUrl(SceneSystem.SceneInstance.RootScene, out var currentUrl);
        var ui = new SampleUI(this, Font);

        var panel = new StackPanel { Orientation = Orientation.Vertical, Width = 180 };
        panel.Children.Add(ui.Heading("Scenes"));
        foreach (var entry in Scenes)
        {
            if (entry.Scene == null || entry.Scene.IsEmpty)
                continue;
            var scene = entry.Scene;
            var button = ui.Button(ui.Label(entry.Name ?? scene.Url), () => SceneSwitch.Load(this, scene), scene.Url == currentUrl);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Margin = new Thickness(0, 6, 0, 0);
            panel.Children.Add(button);
        }
        panel.Children.Add(ui.Text("F1 hides the overlay", 13, SampleUI.Muted, new Thickness(0, 10, 0, 0)));
        SampleUI.Show(this, ui.Panel(panel, HorizontalAlignment.Right));
    }
}
