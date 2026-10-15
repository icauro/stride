using System.Numerics;

namespace Stride.Editor.ImGui;

internal interface IProjectSelectionSource
{
    IReadOnlyList<string> RecentProjects { get; }
    bool ReloadLastSession { get; set; }
    bool OpenRecentProject(string path, Action? accepted = null);
    bool BrowseExistingProject(Action? accepted = null);
    void RecentRow(string path, Vector2 minimum, Vector2 maximum);
    bool RecentContextMenu(string path, Action? accepted = null);
}
