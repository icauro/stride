namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private string? ReadOpenProjectDialogDirectory()
    {
        nativeRecentProjects?.Refresh(true);
        if (nativeRecentProjects?.Error is { } failure) throw new IOException(failure);
        // Empty native values are intentional; only absent keys use legacy data.
        return nativeRecentProjects?.LastOpenSessionDirectory
            ?? preferences?.Current.DialogDirectories.GetValueOrDefault("project");
    }

    private string? ReadAssetImportDialogDirectory(string? legacyDefault)
    {
        nativeRecentProjects?.Refresh(true);
        if (nativeRecentProjects?.Error is { } failure) throw new IOException(failure);
        var stored = nativeRecentProjects?.LastImportDirectory
            ?? preferences?.Current.DialogDirectories.GetValueOrDefault("AssetImport") ?? legacyDefault;
        // Native generators store the first selected file in this string key.
        // Keep its raw value for WPF, but pass the actual containing directory
        // to IFileDialog when it still identifies a real source file.
        return !string.IsNullOrEmpty(stored) && File.Exists(stored) ? Path.GetDirectoryName(Path.GetFullPath(stored)) : stored;
    }

    private void RememberAssetImportSelection(string file)
    {
        var selected = Path.GetFullPath(file);
        nativeRecentProjects?.RememberImportSelection(selected);
        if (preferences == null) return;
        var previous = preferences.Current.DialogDirectories.GetValueOrDefault("AssetImport");
        preferences.Current.DialogDirectories["AssetImport"] = Path.GetDirectoryName(selected)!;
        if (preferences.Save()) return;
        if (previous == null) preferences.Current.DialogDirectories.Remove("AssetImport");
        else preferences.Current.DialogDirectories["AssetImport"] = previous;
        // The native profile may already have saved successfully. Report the
        // genuine compatibility-profile failure rather than undo that save.
        throw new IOException(preferences.Error);
    }
}
