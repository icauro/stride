using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed class ResourceSourceCopyPanel
{
    private enum Step { Copy, Location, Overwrite, InvalidLocation }
    private string[] files = [], resources = [];
    private readonly List<ResourceSourceCopyChoice> copies = [];
    private int index;
    private string folder = "", destination = "", previousDestination = "";
    private bool requestOpen, copyAll, overwriteAll;
    private Step step;
    private Func<string, string?>? pick;
    private Action<ResourceSourceCopyChoice[], string[]>? complete;
    private Action? cancelled;

    internal void Open(string[] sources, string[] roots, string relativeFolder, Func<string, string?> picker,
        Action<ResourceSourceCopyChoice[], string[]> submit, Action cancel)
    {
        Clear(); files = sources.Select(Path.GetFullPath).ToArray(); resources = roots.Select(Path.GetFullPath).ToArray();
        folder = relativeFolder; pick = picker; complete = submit; cancelled = cancel; index = 0; Advance();
    }
    private bool InResources(string path) => resources.Any(root => path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    private void Advance()
    {
        while (index < files.Length)
        {
            if (InResources(files[index])) { index++; continue; }
            if (!copyAll) { step = Step.Copy; requestOpen = true; return; }
            destination = Path.Combine(Path.GetDirectoryName(previousDestination)!, Path.GetFileName(files[index]));
            CheckOverwrite(); if (requestOpen) return;
        }
        var callback = complete; var selected = files.ToArray(); var choices = copies.ToArray(); Clear(); callback?.Invoke(choices, selected);
    }
    private void ChooseLocation()
    {
        if (resources.Length == 0) throw new InvalidOperationException("The package has no resource folder.");
        destination = Path.GetFullPath(Path.Combine(resources[0], folder, Path.GetFileName(files[index])));
        if (!InResources(destination)) throw new InvalidOperationException("The selected directory is not a subdirectory of the resources folder!");
        step = Step.Location; requestOpen = true;
    }
    private void CheckOverwrite()
    {
        previousDestination = destination;
        if (File.Exists(destination) && !overwriteAll) { step = Step.Overwrite; requestOpen = true; return; }
        AcceptCopy();
    }
    private void AcceptCopy()
    {
        copies.Add(new(files[index], destination, File.Exists(destination)));
        files[index] = destination; index++; requestOpen = false;
    }
    internal void Draw(Action<Action> execute, Action<string, string> message)
    {
        const string title = "###ResourceSourceCopy";
        if (requestOpen) { Gui.OpenPopup(title); requestOpen = false; }
        if (!Gui.BeginPopupModal(title, ImGuiWindowFlags.AlwaysAutoResize)) return;
        try
        {
            if (index >= files.Length) { Gui.CloseCurrentPopup(); return; }
            var text = step switch
            {
                Step.Copy => string.Format(EditorLocalization.Text("Source file '{0}' is not inside of your project's resource folders, do you want to copy it?", "Message"), files[index]),
                Step.Location => EditorLocalization.Text("Do you want to place the resource in the default location ?", "Message"),
                Step.Overwrite => string.Format(EditorLocalization.Text("The file '{0}' already exists, it will get overwritten if you continue, do you really want to proceed?", "Message"), destination),
                _ => EditorLocalization.Text("The selected directory is not a subdirectory of the resources folder!", "Message"),
            };
            message("##ResourceSourceCopyMessage", text);
            void Answer(bool yes, bool all = false) => execute(() =>
            {
                Gui.CloseCurrentPopup();
                if (step == Step.Copy)
                {
                    if (!yes) { if (all) index = files.Length; else index++; Advance(); }
                    else { copyAll = all; ChooseLocation(); }
                }
                else if (step == Step.Location || step == Step.InvalidLocation)
                {
                    if (!yes || step == Step.InvalidLocation)
                    {
                        var selected = pick?.Invoke(destination);
                        if (selected != null)
                        {
                            selected = Path.GetFullPath(selected);
                            if (!InResources(selected)) { step = Step.InvalidLocation; requestOpen = true; return; }
                            destination = selected;
                        }
                    }
                    CheckOverwrite(); if (!requestOpen) Advance();
                }
                else
                {
                    if (yes) { overwriteAll = all; AcceptCopy(); } else index++;
                    Advance();
                }
            });
            if (step == Step.InvalidLocation)
            { if (Gui.Button(EditorLocalization.Widget("OK", "Button"))) Answer(false); }
            else
            {
                if (Gui.Button(EditorLocalization.Widget("Yes", "Button"))) Answer(true);
                Gui.SameLine(); if (Gui.Button(EditorLocalization.Widget("No", "Button"))) Answer(false);
                if (index < files.Length - 1 && step != Step.Location)
                {
                    Gui.SameLine(); if (Gui.Button(EditorLocalization.Widget("Yes to all", "Button"))) Answer(true, true);
                    if (step == Step.Copy) { Gui.SameLine(); if (Gui.Button(EditorLocalization.Widget("No to all", "Button"))) Answer(false, true); }
                }
            }
            if (Gui.IsKeyPressed(ImGuiKey.Escape))
            {
                if (step == Step.Copy) Answer(false);
                else if (step == Step.Location) Answer(true);
                else if (step == Step.InvalidLocation) Answer(false);
                else execute(() => { Gui.CloseCurrentPopup(); var callback = cancelled; Clear(); callback?.Invoke(); });
            }
        }
        finally { Gui.EndPopup(); }
    }
    internal void Clear()
    {
        files = []; resources = []; copies.Clear(); complete = null; cancelled = null; pick = null;
        copyAll = overwriteAll = requestOpen = false; destination = previousDestination = "";
    }
}
