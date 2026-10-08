using Stride.Engine;
using Stride.Engine.Design;
using Stride.Rendering;
using Stride.UI;
using Stride.UI.Controls;
using Stride.UI.Panels;

namespace ComputeSkinningSample;

/// <summary>UI rows that edit the game-wide <see cref="MeshDeformationSettings"/> live.</summary>
sealed class DeformationControls
{
    private readonly MeshDeformationSettings settings;
    private TextBlock modeHint, batchSizeLabel;

    public DeformationControls(ScriptComponent script)
    {
        // The same instance every ModelRenderProcessor reads, so edits apply to all scenes at once.
        settings = script.Services.GetService<IGameSettingsService>()?.Settings?.GetOrCreateConfiguration<MeshDeformationSettings>()
            ?? script.SceneSystem.SceneInstance.Processors.OfType<ModelRenderProcessor>().FirstOrDefault()?.DeformationSettings
            ?? new MeshDeformationSettings();
        foreach (var processor in script.SceneSystem.SceneInstance.Processors.OfType<ModelRenderProcessor>()) processor.DeformationSettings = settings;
    }

    public void AddTo(StackPanel panel, SampleUI ui)
    {
        panel.Children.Add(ui.Section("Deformation (all scenes)"));
        var modes = Enum.GetValues<MeshDeformationMode>();
        ui.RadioGroup(panel, "Mode", ["Vertex shader", "Compute"], Array.IndexOf(modes, settings.Mode), index => { settings.Mode = modes[index]; Refresh(); });
        panel.Children.Add(modeHint = ui.Text("", 13, SampleUI.Muted, new Thickness(0, 2, 0, 0)));
        panel.Children.Add(batchSizeLabel = ui.Label());
        panel.Children.Add(ui.IntegerSlider(1, 64, settings.BatchSize, value => { settings.BatchSize = value; Refresh(); }));
        var groups = Enum.GetValues<DeformationThreadGroup>();
        ui.RadioGroup(panel, "Thread group", groups.Select(group => group.ToString()).ToArray(), Array.IndexOf(groups, settings.ThreadGroup), index => settings.ThreadGroup = groups[index]);
        Refresh();
    }

    private void Refresh()
    {
        modeHint.Text = settings.Mode switch
        {
            MeshDeformationMode.VertexShader => "Morphs and skinning in the vertex shader of every pass",
            _ => "Morphs and skinning in compute once per frame",
        };
        batchSizeLabel.Text = settings.BatchSize == 1 ? "Batch size: 1 (one dispatch per instance)" : $"Batch size: {settings.BatchSize}";
    }
}
