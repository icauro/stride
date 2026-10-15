using System.Windows.Forms;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private ProjectCreationPanel? projectCreationPanel;
    private ProjectTemplateImageCache? templateImages;
    private void ClearTemplateImages()
    {
        templateImages?.Dispose(); templateImages = null;
    }
    private void DrawTemplateImage(string path, float maximumWidth, bool allowEnlarge)
    {
        if (renderer == null || IsEditorGraphicsRecovering) return;
        var cache = templateImages ??= new(GraphicsDevice, renderer, RetireEditorPreview);
        if (!cache.TryGet(path, frames, out var reference, out var size)) return;
        var factor = maximumWidth / Math.Max(size.X, size.Y);
        if (!allowEnlarge) factor = Math.Min(1, factor);
        factor *= Hexa.NET.ImGui.ImGui.GetStyle().FontScaleDpi;
        Hexa.NET.ImGui.ImGui.Image(reference, size * factor);
    }
    private bool IsProjectCreationUpdating => projectCreationPanel?.IsGenerating == true;
    private void PumpCancelledProjectCreation()
    {
        projectCreationPanel?.PumpCancelledGeneration();
        addedProjectCreation?.PumpCancelledGeneration();
    }
    private ProjectCreationPanel ProjectCreation => projectCreationPanel ??= CreateProjectCreationPanel(
        path => RequestReplace(() => StartOpenProject(path), true, true));
    private ProjectCreationPanel CreateProjectCreationPanel(Action<string> completed, bool existingSolution = false) => new(
        path => Services.Commands.TryExecute(() => completed(path)), BrowseProjectLocation,
        (id, text) => DrawSelectableText(id, text), existingSolution
            ? () => workspace?.Session.Packages.Select(package => package.Meta.Name).OfType<string>() ?? [] : null, existingSolution, DrawTemplateImage,
            ReadProjectCreationLocation,
            existingSolution ? null : RememberProjectCreationLocation, new ProjectCreationPresentation(this), () => statusMessage,
            existingSolution ? null : new ProjectSelectionSource(this),
            message => outputLog.Append("Project", message.Type, message.Module, message.Text, message.ExceptionInfo?.ToString()),
            failure => EditorGraphicsFailures.ThrowIfDeviceLost(GraphicsDevice, failure), existingSolution ? () => workspace?.Session : null);
    private void RememberProjectCreationLocation(string location)
    {
        nativeRecentProjects?.RememberNewSessionDirectory(location);
        if (preferences == null) return;
        preferences.Current.DialogDirectories["new-project"] = location;
        SaveEditorPreferences();
    }
    private string? ReadProjectCreationLocation()
    {
        nativeRecentProjects?.Refresh(true);
        if (nativeRecentProjects?.Error is { } failure) throw new IOException(failure);
        return nativeRecentProjects?.LastNewSessionDirectory ?? preferences?.Current.DialogDirectories.GetValueOrDefault("new-project");
    }
    private string? BrowseProjectLocation(string initial)
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = initial, Description = EditorLocalization.Text("Browse directory", "ToolTip"), UseDescriptionForTitle = true };
        return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null;
    }
    private void DrawProjectCreationDialog() => projectCreationPanel?.Draw();
}
