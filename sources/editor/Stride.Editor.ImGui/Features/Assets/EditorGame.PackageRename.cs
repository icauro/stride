using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Stride.Core.IO;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly AssetFolderRenameState packageRename = new();
    private Package? renamingPackage;
    private WeakReference<ProjectWorkspace>? renamingPackageWorkspace;
    private string? renamingPackagePath;
    private Func<bool>? packageRenameCurrent;

    private bool CanRenameStandalonePackage(AssetBrowserFolder folder)
        => CanStartAssetEdit && folder.Parent == null && folder.Package.Container is not SolutionProject
            && IsCurrentEditableAssetPackage(folder.Package) && ReferenceEquals(assetBrowser.FindFolder(folder.Id)?.Package, folder.Package)
            && (!assetBrowser.IsFolderSelected(folder) || assetBrowser.CaptureNavigationScope() is { Categories.Length: 0 } scope
                && scope.Folders.All(id => assetBrowser.FindFolder(id) is { } location && IsCurrentEditableAssetPackage(location.Package)
                    && (location.Parent == null || location.RelativePath.Length > 0)));
    private void RequestStandalonePackageRename(AssetBrowserFolder folder, Func<bool>? isCurrent = null)
    {
        if (!CanRenameStandalonePackage(folder) || isCurrent?.Invoke() == false) return;
        ValidatePendingPropertyStrings();
        packageRename.Begin(folder); renamingPackage = folder.Package; packageRenameCurrent = isCurrent;
        renamingPackageWorkspace = new(workspace!); renamingPackagePath = folder.Package.FullPath.ToOSPath();
    }
    private void ClearStandalonePackageRename()
    { packageRename.Clear(); renamingPackage = null; renamingPackageWorkspace = null; renamingPackagePath = null; packageRenameCurrent = null; }
    private bool DrawStandalonePackageRenameField(AssetBrowserFolder folder, float width)
    {
        if (packageRename.Id != folder.Id) return false;
        if (renamingPackageWorkspace == null || !renamingPackageWorkspace.TryGetTarget(out var expected))
        { ClearStandalonePackageRename(); return false; }
        if (!CanRenameStandalonePackage(folder) || packageRenameCurrent?.Invoke() == false || !ReferenceEquals(renamingPackage, folder.Package)
            || !ReferenceEquals(workspace, expected)
            || !string.Equals(folder.Package.FullPath.ToOSPath(), renamingPackagePath, StringComparison.Ordinal))
        { ClearStandalonePackageRename(); return false; }
        if (packageRename.Focus) { Gui.SetKeyboardFocusHere(); packageRename.Focus = false; }
        Gui.SetNextItemWidth(width);
        var commit = NativeStringInput.Draw("##RenamePackage", ref packageRename.Text,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll, selectAllOnFocus: true);
        if (Gui.IsKeyPressed(ImGuiKey.Escape) && Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) ClearStandalonePackageRename();
        else if (commit || Gui.IsItemDeactivatedAfterEdit())
        {
            var name = packageRename.Text; var source = renamingPackagePath!; var version = assetBrowser.SelectionVersion; var isCurrent = packageRenameCurrent;
            ClearStandalonePackageRename();
            pendingAssetRelocation = () =>
            {
                if (isCurrent?.Invoke() != false && ReferenceEquals(workspace, expected) && assetBrowser.SelectionVersion == version
                    && string.Equals(folder.Package.FullPath.ToOSPath(), source, StringComparison.Ordinal)) RenameStandalonePackage(folder, name);
            };
        }
        return true;
    }
    private void RenameStandalonePackage(AssetBrowserFolder folder, string name)
    {
        if (!CanRenameStandalonePackage(folder)) return;
        string? error = name.Length > 240 ? EditorLocalization.Text("The name is too long.", "Message")
            : name.Contains(UPath.DirectorySeparatorChar) || name.Contains(UPath.DirectorySeparatorCharAlt) || !UPath.IsValid(name)
                ? EditorLocalization.Text("The name contains invalid characters.", "Message")
            : name.Length == 0 ? EditorLocalization.Text("The name is empty.", "Message") : null;
        if (error == null && workspace!.Session.Packages.Any(package => !ReferenceEquals(package, folder.Package)
            && string.Equals(package.Container is SolutionProject project ? project.Name : Path.GetFileNameWithoutExtension(package.FullPath.ToOSPath()), name, StringComparison.InvariantCultureIgnoreCase)))
            error = EditorLocalization.Text("A package with the same name already exists in the session.", "Message");
        if (error != null)
        {
            RequestAssetActionMessage(string.Format(EditorLocalization.Text("This package couldn't be renamed. {0}", "Message"), error));
            return;
        }
        var package = folder.Package; var before = Path.GetFullPath(package.FullPath.ToOSPath());
        var after = Path.Combine(Path.GetDirectoryName(before)!, name + Path.GetExtension(before));
        if (before == after) return;
        ValidatePendingPropertyStrings(); documents.CommitAll();
        var current = workspace!; var history = sessionHistory!;
        if (!CanRenameStandalonePackage(folder) || !ReferenceEquals(workspace, current)) return;
        var metadata = history.Registry.GetOrCreate(before, null, () => new PackageMetadataDocument(current, package, history));
        var user = GetPackageSettingsDocument(package, history);
        var settings = history.Registry.Find(user.Path) as EditorAuthoringState<PackageSettingsDocument>
            ?? throw new InvalidOperationException("The package settings have no canonical rename owner.");
        var operation = new StandalonePackageRenameOperation(current, package, history, metadata, settings, after, RefreshRenamedPackage, retry =>
        { if (!packageCommandCleanup.Contains(retry)) packageCommandCleanup.Add(retry); });
        using var capture = history.CaptureOperationsAtomic(() =>
        {
            using var transaction = history.CreateTransaction("Rename package");
            history.PushOperation(operation, metadata, settings);
            operation.ApplyInitial();
        });
        capture.Publish(history, $"Rename package '{Path.GetFileNameWithoutExtension(before)}' to '{name}'");
    }
    private void RefreshRenamedPackage(Package package, string previous, string next)
    {
        if (workspace == null || !ReferenceEquals(package.Session, workspace.Session)) throw new InvalidOperationException("The renamed package session changed.");
        var opened = string.Equals(workspace.OpenPath, previous, StringComparison.OrdinalIgnoreCase);
        workspace.RetargetStandalonePackage(package, previous, next);
        if (opened && preferences != null)
        {
            for (var index = 0; index < preferences.Current.RecentProjects.Count; index++)
                if (SamePath(preferences.Current.RecentProjects[index], previous)) preferences.Current.RecentProjects[index] = next;
            if (!SamePath(previous, next) && preferences.Current.ActiveProjects.Remove(previous, out var active)) preferences.Current.ActiveProjects[next] = active;
            SaveEditorPreferences();
        }
        assetBrowser.RefreshPackageRename(workspace, previous, next);
        ClearSolutionExplorerWorkspace();
        if (ReferenceEquals(inspectedPackage, package)) RequestPackageProperties(package);
    }
}
