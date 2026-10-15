using System.Text.RegularExpressions;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private HashSet<string>? migratedWindowTitles;
    private readonly EditorNativeDockSelectionRestore nativeDockSelectionRestore = new();
    private EditorDocumentGroupsLayout? restoredDocumentGroupLayout, retainedDocumentGroupLayout;
    private void CaptureDocumentGroupLayout(EditorDocumentGroupPanel panel, string workspaceIdentity)
        => retainedDocumentGroupLayout = panel.CaptureLayout(workspaceIdentity, documents.Active?.Path);

    private bool TryRestoreDocumentGroupLayout(EditorDocumentGroupPanel panel, string workspaceIdentity)
    {
        var layout = restoredDocumentGroupLayout;
        if (layout == null || !string.Equals(layout.Workspace, workspaceIdentity, StringComparison.OrdinalIgnoreCase)) return false;
        if (layout.ActivePath is { } path)
        {
            var active = documents.Find(path) ?? throw new InvalidDataException("The saved active document is not open.");
            ActivateDocument(active);
        }
        panel.RestoreLayout(layout, workspaceIdentity);
        retainedDocumentGroupLayout = layout; restoredDocumentGroupLayout = null;
        return true;
    }
    private void MigrateLiveWindowTitle(string previous, string current)
    {
        migratedWindowTitles ??= [];
        if (!migratedWindowTitles.Add(current)) return;
        var oldWindow = ImGuiP.FindWindowByName(previous);
        if (!oldWindow.IsNull && oldWindow.DockId != 0)
            Gui.SetNextWindowDockID(oldWindow.DockId, ImGuiCond.Always);
    }
    private bool LoadEditorLayout(string path)
    {
        string layout;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 4 * 1024 * 1024) throw new InvalidDataException("Editor layout exceeds the supported size.");
            layout = File.ReadAllText(path);
            if (layout.IndexOf('\0') >= 0 || !layout.Contains("[Window][", StringComparison.Ordinal))
                throw new InvalidDataException("Editor layout contains no usable window settings.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine("Could not restore editor layout: " + error.Message);
            return false;
        }
        return LoadEditorLayoutFromMemory(layout, null);
    }

    private bool LoadEditorLayoutFromMemory(string layout, EditorDocumentGroupsLayout? groups)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(layout) > 4 * 1024 * 1024
            || layout.IndexOf('\0') >= 0 || !layout.Contains("[Window][", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Could not restore editor layout: invalid window settings.");
            return false;
        }
        EditorDocumentGroupsLayout? extractedGroups = null;
        try { layout = EditorDocumentGroupLayoutCodec.Extract(layout, out extractedGroups); }
        catch (Exception error) when (error is InvalidDataException or FormatException or System.Text.Json.JsonException)
        {
            // Invalid membership must not prevent the existing native docking/title migration recovery.
            Console.Error.WriteLine("Could not restore document groups: " + error.Message);
        }
        layout = layout.Replace("[Window][Inspector]", "[Window][Property grid###Inspector]", StringComparison.Ordinal)
            .Replace("[Window][Asset errors]", "[Window][###Asset errors]", StringComparison.Ordinal);
        var legacy = Regex.Match(layout, @"(?m)^\[Window\]\[Console\]\r?\n[^\[]*");
        if (legacy.Success)
        {
            var replacement = "";
            // Both independent windows inherit only the old workspace placement.
            // Other window sections and the complete docking node tree stay intact.
            if (!layout.Contains("[Window][Asset view]", StringComparison.Ordinal))
                replacement += legacy.Value.Replace("[Window][Console]", "[Window][Asset view]", StringComparison.Ordinal);
            if (!layout.Contains("[Window][Output]", StringComparison.Ordinal))
                replacement += legacy.Value.Replace("[Window][Console]", "[Window][Output]", StringComparison.Ordinal);
            layout = layout.Remove(legacy.Index, legacy.Length).Insert(legacy.Index, replacement);
        }
        try { layout = EditorWindowIdentityMigration.Migrate(layout); }
        catch (Exception error) when (error is InvalidDataException or RegexMatchTimeoutException)
        {
            Console.Error.WriteLine("Could not migrate editor window identities: " + error.Message);
            return false;
        }
        Gui.LoadIniSettingsFromMemory(layout);
        nativeDockSelectionRestore.Capture(layout);
        restoredDocumentGroupLayout = groups ?? extractedGroups;
        return true;
    }

    private void SaveEditorLayout(string path)
    {
        if (documents.Entries.Count != 0) CaptureCurrentDocumentGroupLayout();
        var directory = Path.GetDirectoryName(path)!;
        var pending = Path.Combine(directory, ".layout-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(directory);
            Gui.SaveIniSettingsToDisk(pending);
            var layout = EditorDocumentGroupLayoutCodec.Append(File.ReadAllText(pending), retainedDocumentGroupLayout ?? restoredDocumentGroupLayout);
            if (System.Text.Encoding.UTF8.GetByteCount(layout) > 4 * 1024 * 1024)
                throw new InvalidDataException("Editor layout exceeds the supported size.");
            File.WriteAllText(pending, layout, new System.Text.UTF8Encoding(false));
            // A crash during serialization must not replace the last complete layout.
            using (var stream = new FileStream(pending, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                stream.Flush(true);
            File.Move(pending, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Could not save editor layout: " + error.Message);
        }
        finally
        {
            try { if (File.Exists(pending)) File.Delete(pending); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Console.Error.WriteLine("Could not remove pending editor layout: " + error.Message); }
        }
    }
}
