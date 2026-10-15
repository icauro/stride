using System.Numerics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class ProjectSelectionSource(EditorGame game) : IProjectSelectionSource
    {
        public IReadOnlyList<string> RecentProjects => game.PresentedRecentProjects();
        public bool ReloadLastSession
        {
            get => game.ReadReloadLastSession();
            set => game.Execute(() => game.WriteReloadLastSession(value));
        }
        public bool OpenRecentProject(string path, Action? accepted = null)
        {
            if (game.ResolveRecentProjectPath(path) == null) return false;
            return game.Services.Commands.TryExecute(() => { accepted?.Invoke(); game.RequestRecentProjectOpen(path); });
        }
        public bool BrowseExistingProject(Action? accepted = null)
        {
            var requested = false;
            game.Execute(() =>
            {
                var directory = game.ReadOpenProjectDialogDirectory();
                var path = WindowsFileDialogs.Open(game.GetCurrentDialogOwner(), "Open Stride project or solution",
                    "Stride projects and solutions\0*.sdpkg;*.csproj;*.sln;*.slnx\0Solutions\0*.sln;*.slnx\0Stride packages\0*.sdpkg\0C# projects\0*.csproj\0All files\0*.*\0\0", directory, game.projectPathInput);
                if (path == null) return;
                accepted?.Invoke();
                game.projectPathInput = path;
                game.RequestReplace(() => game.StartOpenProject(path), true, true);
                requested = true;
            });
            return requested;
        }
        public void RecentRow(string path, Vector2 minimum, Vector2 maximum) => game.PaintRecentProjectRow(path, minimum, maximum);
        public bool RecentContextMenu(string path, Action? accepted = null)
        {
            var opened = false;
            Action? action = null;
            if (!Gui.BeginPopupContextItem("##RecentProjectCommands")) return false;
            try
            {
                var bold = game.PushPackagePropertiesBoldFont(true);
                try
                {
                    if (game.DrawNativeMenuItem(EditorLocalization.Widget("Open", "Menu"))) action = () => { accepted?.Invoke(); game.RequestRecentProjectOpen(path); opened = true; };
                }
                finally { if (bold) Gui.PopFont(); }
                Gui.Separator();
                if (game.DrawNativeMenuItem(EditorLocalization.Widget("Show in Explorer", "Menu"))) action = () => game.ExploreRecentProject(path);
                if (game.DrawNativeMenuItem(EditorLocalization.Widget("Copy solution path", "Menu"))) action = () => game.CopyRecentProjectPath(path, false);
                if (game.DrawNativeMenuItem(EditorLocalization.Widget("Copy folder path", "Menu"))) action = () => game.CopyRecentProjectPath(path, true);
                Gui.Separator();
                if (game.DrawNativeMenuItem(EditorLocalization.Widget("Remove From List", "Menu"))) action = () => game.RemoveRecentProject(path);
            }
            finally { Gui.EndPopup(); }
            // Native action dispatch follows popup scope unwind; the host helper
            // rechecks the actual persisted MRU path, including after consent.
            if (action != null) game.Execute(action);
            return opened;
        }
    }

    private void PaintRecentProjectRow(string path, Vector2 minimum, Vector2 maximum)
    {
        // ExistingProjectViewModel.Icon is genuinely null; retain its empty
        // image region rather than substituting a new-template thumbnail.
        var scale = Gui.GetStyle().FontScaleDpi;
        var origin = minimum + new Vector2(70, 2) * scale;
        var width = Math.Max(1, maximum.X - origin.X - 2 * scale);
        var name = Path.GetFileNameWithoutExtension(path);
        if (!CanDrawNativeWidgetText)
        {
            var draw = Gui.GetWindowDrawList(); draw.PushClipRect(origin, maximum, true);
            try
            {
                draw.AddText(origin, Gui.GetColorU32(ImGuiCol.Text), name);
                draw.AddText(origin + new Vector2(0, Gui.GetTextLineHeight()), Gui.GetColorU32(ImGuiCol.Text), path);
            }
            finally { draw.PopClipRect(); }
            return;
        }
        var text = nativeWidgetText ??= new();
        var title = text.Get(name, 16 * scale, scale, width, false, NativeWidgetWeight, Gui.GetColorU32(ImGuiCol.Text));
        PaintNativeWidget(title, origin, origin, maximum); LogNativeWidgetText(name, origin);
        var descriptionOrigin = origin + new Vector2(0, title.Height);
        var height = Math.Max(0, maximum.Y - descriptionOrigin.Y - 2 * scale);
        if (height == 0) return;
        Vector2 Measure(string value) => text.Get(value, Gui.GetFontSize(), scale, width, true, NativeWidgetWeight, Gui.GetColorU32(ImGuiCol.Text)).Size;
        var fitted = FitTemplateDescription(path, height, Measure);
        var description = text.Get(fitted, Gui.GetFontSize(), scale, width, true, NativeWidgetWeight, Gui.GetColorU32(ImGuiCol.Text));
        PaintNativeWidget(description, descriptionOrigin, descriptionOrigin, maximum - new Vector2(2 * scale));
        LogNativeWidgetText(path, descriptionOrigin);
    }
}
