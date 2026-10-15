using Stride.Assets.Scripts;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    // Read-only eligibility: inspecting a journal never publishes its payload.
    private void ValidateRecoveryWorkspaceDependencies(EditorRecoveryWorkspaceState state)
    {
        if (GetRecoveryWorkspaceDependencyFailure(state) is { } failure) throw new InvalidOperationException(failure);
    }
    private string? GetRecoveryWorkspaceDependencyFailure(EditorRecoveryWorkspaceState state)
    {
        var current = workspace ?? throw new InvalidOperationException("No recovery workspace is loaded.");
        if (!SamePath(state.ProjectPath, current.OpenPath))
            throw new InvalidOperationException("The recovery dependency journal has another workspace owner.");
        foreach (var descriptor in state.Documents.Where(document => document.GeneratedByAssetId != null))
        {
            var id = new AssetId(Guid.Parse(descriptor.GeneratedByAssetId!));
            if (current.Session.FindAsset(id) != null) continue;
            if (!TryReadNewAssetRecoveryDependency(descriptor, out var entry) || entry == null
                || entry.AssetId != id.ToString() || entry.Kind != "visual-script" || entry.BaselineHash.Length != 0
                || entry.PackagePath == null || descriptor.PackagePath == null || !SamePath(entry.PackagePath, descriptor.PackagePath)
                || entry.Namespace != descriptor.Namespace || entry.AssetLocation == null
                || File.Exists(entry.Path) || Directory.Exists(entry.Path) || authoring?.Find(entry.Path) != null)
                throw new InvalidOperationException("The missing generated producer has no exact new-asset recovery dependency.");
            var package = current.Session.LocalPackages.SingleOrDefault(package => SamePath(package.FullPath.ToOSPath(), entry.PackagePath));
            if (package == null || package.IsReadOnly || package.Container is not SolutionProject project
                || descriptor.SourceProjectPath == null || !SamePath(project.FullPath.ToOSPath(), descriptor.SourceProjectPath))
                throw new InvalidOperationException("The generated producer dependency has another loaded project owner.");
            var record = entry; // Panel lookup verifies durable payload hash and journal boundary.
            var loaded = EditorRecoveryDocuments.LoadRecoveryAsset(record, new LoggerResult());
            if (loaded.Asset is not VisualScriptAsset || loaded.Asset.Id != id)
                throw new InvalidOperationException("The generated producer dependency is not its real visual-script payload.");
            var url = new UFile(record.AssetLocation!);
            var item = AssetItem.CreateForPackage(package, package.Container?.Unqualify(url) ?? url, loaded.Asset);
            item.SourceFolder = record.SourceFolder == null ? package.GetDefaultAssetFolder() : new UDirectory(record.SourceFolder);
            if (!SamePath(item.FullPath.ToOSPath(), entry.Path) || !SamePath(item.GetGeneratedAbsolutePath().ToOSPath(), descriptor.Path))
                throw new InvalidOperationException("The generated producer dependency does not own its actual input and output paths.");
            return "Restore the visual-script asset before restoring its workspace.";
        }
        return null;
    }
}
