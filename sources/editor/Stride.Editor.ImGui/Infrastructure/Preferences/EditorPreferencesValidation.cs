using System.Text.Json;

namespace Stride.Editor.ImGui;

/// <summary>Preference schema policy, independent of disk publication and widgets.</summary>
internal static class EditorPreferencesValidation
{
    public static EditorPreferences Validate(EditorPreferences? value)
    {
        if (value == null || value.Version != 1 || !Enum.IsDefined(value.Theme) || !Enum.IsDefined(value.Language) || string.IsNullOrWhiteSpace(value.DefaultIDE)
            || value.GraphicsApi is not ("Default" or "Direct3D11" or "Direct3D12" or "Vulkan")
            || string.IsNullOrWhiteSpace(value.DefaultTextEditor) || string.IsNullOrWhiteSpace(value.ShaderEditor)
            || !Enum.IsDefined(value.DebugOutputLevel) || value.DebugOutputModuleLevels == null || value.SourceModuleLevels == null
            || value.DebugOutputModuleLevels.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || !Enum.IsDefined(pair.Value))
            || value.SourceModuleLevels.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || !Enum.IsDefined(pair.Value))
            || !Enum.IsDefined(value.AssetSort) || value.AssetDisplayMode is { } displayMode && !Enum.IsDefined(displayMode)
            || value.AssetTileThumbnailSize is < 16 or > 128 || value.AssetGridThumbnailSize is < 16 or > 128
            || value.ScriptEditorFontSize is < 8 or > 72
            || value.CameraBindings == null || !value.CameraBindings.IsValid
            || !ValidSnap(value.DefaultTranslationSnap) || !ValidSnap(value.DefaultRotationSnap) || !ValidSnap(value.DefaultScaleSnap)
            || value.DialogDirectories == null || value.RecentProjects == null || value.ActiveProjects == null
            || value.BuildTargetPaths == null || value.BuildConfigurations == null || value.AssetFilters == null
            || value.BuildTargetPaths.Any(pair => !Absolute(pair.Key) || !Absolute(pair.Value))
            || value.BuildConfigurations.Any(pair => !Absolute(pair.Key) || string.IsNullOrWhiteSpace(pair.Value) || pair.Value.IndexOfAny([';', '\n', '\r']) >= 0)
            || value.DialogDirectories.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || !Absolute(pair.Value))
            || value.LastProjectPath != null && !Absolute(value.LastProjectPath)
            || !string.IsNullOrEmpty(value.DefaultSessionPath) && !Absolute(value.DefaultSessionPath)
            || value.DefaultSessionEditorVersion != null && string.IsNullOrWhiteSpace(value.DefaultSessionEditorVersion)
            || value.RecentProjects.Any(item => !Absolute(item))
            || value.ActiveProjects.Any(pair => !Absolute(pair.Key) || !Absolute(pair.Value))
            || value.AssetFilters.Any(filter => filter == null || filter.Category is not ("Name" or "Type" or "Tag") || string.IsNullOrEmpty(filter.Value))
            || value.ReferenceFilters != null && value.ReferenceFilters.Any(filter => filter == null || filter.Category is not ("Name" or "Type" or "Tag") || string.IsNullOrEmpty(filter.Value))
            || value.RemotePort is < 1 or > 65535 || value.RemoteHost == null || value.RemoteUsername == null
            || value.RemotePassword == null || value.RemoteLocation == null || value.RemoteDisplay == null)
            throw new JsonException("Invalid or unsupported editor preferences.");
        return value;
    }

    public static void RestoreComparers(EditorPreferences value)
    {
        value.BuildTargetPaths = new(value.BuildTargetPaths, StringComparer.OrdinalIgnoreCase);
        value.BuildConfigurations = new(value.BuildConfigurations, StringComparer.OrdinalIgnoreCase);
        value.ActiveProjects = new(value.ActiveProjects, StringComparer.OrdinalIgnoreCase);
        value.DialogDirectories = new(value.DialogDirectories, StringComparer.Ordinal);
        value.DebugOutputModuleLevels = new(value.DebugOutputModuleLevels, StringComparer.Ordinal);
        value.SourceModuleLevels = new(value.SourceModuleLevels, StringComparer.Ordinal);
    }

    private static bool Absolute(string? value) => !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value);
    private static bool ValidSnap(float value) => float.IsFinite(value) && value > 0;
}
