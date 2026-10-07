using Stride.Engine;
using Stride.Engine.Design;
using Stride.Rendering;
using Stride.UI.Controls;
using Stride.UI.Panels;

namespace ComputeSkinningSample;

/// <summary>UI rows that edit the game-wide <see cref="MeshDeformationSettings"/> live.</summary>
sealed class DeformationControls
{
    private readonly MeshDeformationSettings settings;
    private TextBlock modeLabel, batchSizeLabel, groupLabel;

    public DeformationControls(ScriptComponent script)
    {
        // The same instance every ModelRenderProcessor reads, so edits apply to all scenes at once.
        settings = script.Services.GetService<IGameSettingsService>()?.Settings?.GetOrCreateConfiguration<MeshDeformationSettings>()
            ?? script.SceneSystem.SceneInstance.Processors.OfType<ModelRenderProcessor>().FirstOrDefault()?.DeformationSettings
            ?? new MeshDeformationSettings();
        foreach (var processor in script.SceneSystem.SceneInstance.Processors.OfType<ModelRenderProcessor>()) processor.DeformationSettings = settings;
    }

    public void AddTo(StackPanel panel, Func<TextBlock> label, Func<TextBlock, Action, Button> toggle, Func<int, int, int, Action<int>, Slider> slider)
    {
        panel.Children.Add(toggle(modeLabel = label(), () => { settings.Mode = Next(settings.Mode); Refresh(); }));
        panel.Children.Add(batchSizeLabel = label());
        panel.Children.Add(slider(1, 64, settings.BatchSize, value => { settings.BatchSize = value; Refresh(); }));
        panel.Children.Add(toggle(groupLabel = label(), () => { settings.ThreadGroup = Next(settings.ThreadGroup); Refresh(); }));
        Refresh();
    }

    private static T Next<T>(T value) where T : struct, Enum
    {
        var values = Enum.GetValues<T>();
        return values[(Array.IndexOf(values, value) + 1) % values.Length];
    }

    private void Refresh()
    {
        modeLabel.Text = settings.Mode switch
        {
            MeshDeformationMode.Auto => "Deformation: Auto (compute skinning for shadow casters)",
            MeshDeformationMode.ComputeMorph => "Deformation: Compute morph + vertex-shader skinning",
            _ => "Deformation: Compute morph + skinning",
        };
        batchSizeLabel.Text = settings.BatchSize == 1 ? "Batch size: 1 (one dispatch per instance)" : $"Batch size: {settings.BatchSize}";
        groupLabel.Text = $"Thread group: {settings.ThreadGroup}";
    }
}
