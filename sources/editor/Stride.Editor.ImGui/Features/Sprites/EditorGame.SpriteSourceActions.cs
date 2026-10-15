using System.Diagnostics;
using Hexa.NET.ImGui;
using Stride.Core.Extensions;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly Queue<string> spriteSourceActionMessages = new();
    private string? spriteSourceActionMessage;
    private void EditSpriteSourceImage(AssetMetadataDocument document, SpriteSettingFrame frame, bool explore)
    {
        try
        {
            var path = string.IsNullOrEmpty(frame.Value.Source) ? "" : Path.GetFullPath(frame.Value.Source, Path.GetDirectoryName(document.Path)!);
            if (!File.Exists(path))
            { spriteSourceActionMessages.Enqueue(EditorLocalization.Text("Couldn't find the file", "Message")); return; }
            var start = new ProcessStartInfo { FileName = explore ? "explorer.exe" : path, UseShellExecute = true };
            if (explore) { start.ArgumentList.Add("/select,"); start.ArgumentList.Add(path); }
            else start.Verb = start.Verbs.FirstOrDefault(verb => verb.Equals("edit", StringComparison.OrdinalIgnoreCase));
            using var process = Process.Start(start);
        }
        catch (Exception error)
        {
            spriteSourceActionMessages.Enqueue(string.Format(EditorLocalization.Text(explore
                ? "There was a problem opening Explorer.{0}" : "There was a problem while editing the image.{0}", "Message"), error.FormatSummary(true)));
        }
    }
    private void DrawSpriteSourceActionMessage()
    {
        var title = EditorLocalization.Widget("Stride Game Studio") + "###SpriteSourceActionMessage";
        if (spriteSourceActionMessage == null && spriteSourceActionMessages.TryDequeue(out var message))
        { spriteSourceActionMessage = message; Gui.OpenPopup(title); }
        using var modal = ImGuiScopes.Modal(title, ImGuiWindowFlags.AlwaysAutoResize);
        if (!modal.DrawContents) return;
        if (spriteSourceActionMessage == null) { Gui.CloseCurrentPopup(); return; }
        DrawSelectableText("##Message", spriteSourceActionMessage);
        if (DrawNativeButton(EditorLocalization.Widget("OK", "Button")) || Gui.IsKeyPressed(ImGuiKey.Enter) || Gui.IsKeyPressed(ImGuiKey.Escape))
        { spriteSourceActionMessage = null; Gui.CloseCurrentPopup(); }
    }
    private void ClearSpriteSourceActions()
    { spriteSourceActionMessages.Clear(); spriteSourceActionMessage = null; }
}
