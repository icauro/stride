using System.Diagnostics;
using Stride.Core.Assets;
using Stride.Core.Extensions;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Func<bool> CaptureExternalAssetIntent(AssetItem item, string path)
    {
        var capturedWorkspace = workspace == null ? null : new WeakReference<ProjectWorkspace>(workspace);
        var capturedItem = new WeakReference<AssetItem>(item);
        var id = item.Id;
        return () => capturedWorkspace != null && capturedWorkspace.TryGetTarget(out var owner)
            && ReferenceEquals(workspace, owner) && capturedItem.TryGetTarget(out var current) && !current.IsDeleted
            && ReferenceEquals(owner.Session.FindAsset(id), current) && SamePath(current.FullPath.ToOSPath(), path);
    }

    private void OpenAssetSourceExternally(AssetItem item)
    {
        var intent = CaptureExternalAssetIntent(item, item.FullPath.ToOSPath());
        if (!intent()) return;
        var source = AssetMainSource(item);
        if (source == null)
        { RequestAssetExploreMessage("This asset doesn't have a source file to open."); return; }
        OpenExternalFile(source, intent);
    }

    private void OpenAssetFileExternally(string path, bool textEditor, Func<bool> isCurrent)
    {
        if (!isCurrent()) return;
        if (!textEditor) { OpenExternalFile(path, isCurrent); return; }
        var settings = preferences?.Current ?? FallbackSceneEditorPreferences;
        var extension = Path.GetExtension(path);
        var shader = extension.Equals(".sdsl", StringComparison.OrdinalIgnoreCase) || extension.Equals(".sdfx", StringComparison.OrdinalIgnoreCase);
        var editor = shader ? settings.ShaderEditor : settings.DefaultTextEditor;
        if (!File.Exists(path))
        {
            // Native OpenWithTextEditor awaits information and still permits the editor to open a new file.
            RequestAssetActionMessage(EditorLocalization.Text("You need to save the file before you can open it.", "Message"), acknowledged: StartEditor);
            return;
        }
        StartEditor();
        void StartEditor()
        {
            if (!isCurrent()) return;
            try { ExternalTextEditorLauncher.Open(path, editor, requireExistingFile: false); }
            catch (Exception error)
            {
                var native = error.InnerException ?? error;
                RequestAssetActionMessage(EditorLocalization.Text("There was a problem starting the text editor. Make sure the path to the text editor in Settings is correct.", "Message")
                    + native.FormatSummary(true), true);
            }
        }
    }

    private void OpenExternalFile(string path, Func<bool> isCurrent)
    {
        if (!isCurrent()) return;
        try
        {
            if (!File.Exists(path))
            { RequestAssetExploreMessage("You need to save the file before you can open it."); return; }
            var start = new ProcessStartInfo { FileName = path, UseShellExecute = true };
            start.Verb = start.Verbs.FirstOrDefault(verb => verb.Equals("edit", StringComparison.OrdinalIgnoreCase));
            using var process = Process.Start(start);
        }
        catch (Exception error)
        { RequestAssetActionMessage(EditorLocalization.Text("An error occurred while opening the file.", "Message") + error.FormatSummary(true), true); }
    }
}
