using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record PreparedScriptSourceMove(AssetEntry Entry, string Url);
    private PreparedScriptSourceMove PrepareScriptCodeActionMove(ScriptCodeActionSourceEdit edit)
    {
        var destination = edit.DestinationPath ?? throw new InvalidOperationException("The source action has no move destination.");
        var project = workspace!.Projects.SingleOrDefault(project => edit.ProjectPath != null && SamePath(project.FullPath.ToOSPath(), edit.ProjectPath))
            ?? throw new InvalidOperationException("The source move has no native project owner.");
        var item = project.Package.Assets.SingleOrDefault(item => item.Asset is SourceCodeAsset && SamePath(item.FullPath.ToOSPath(), edit.Path))
            ?? throw new InvalidOperationException("The source move has no native package asset owner.");
        var entry = workspace.Assets.SingleOrDefault(entry => entry.Id == item.Id)
            ?? throw new InvalidOperationException("The source move has no asset browser identity.");
        _ = GetCanonicalDocument(edit.Path, () => CreateScriptDocument(edit.Path)) as ScriptSourceDocument
            ?? throw new InvalidOperationException("The source move has another canonical document owner.");
        var caseOnly = SamePath(edit.Path, destination);
        var sourceOwner = authoring!.Find(edit.Path);
        var destinationOwner = authoring.Find(destination);
        if (!CanRelocateAsset(entry) || !caseOnly && (File.Exists(destination) || Directory.Exists(destination) || destinationOwner != null)
            || caseOnly && (sourceOwner?.AssetId != item.Id || !ReferenceEquals(sourceOwner, destinationOwner)))
            throw new InvalidOperationException("The source move destination is unavailable.");
        if (caseOnly)
        {
            if (sourceOwner!.Document is not ScriptSourceDocument source || !source.CanEditText || !SamePath(source.Path, edit.Path))
                throw new InvalidOperationException("The source move has no matching canonical identity.");
            if (!source.DiskContentHash.IsEmpty)
            {
                using var owned = new WindowsOwnedAssetFileRename(edit.Path, source.DiskContentHash.Span);
            }
            else if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException("The unsaved source move destination is occupied.");
        }
        var sourceRoot = Path.GetFullPath(Path.Combine(project.Package.RootDirectory.ToOSPath(), item.SourceFolder?.ToOSPath() ?? ""));
        var relative = Path.GetRelativePath(sourceRoot, destination);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal)
            || !Path.GetExtension(relative).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The source move leaves its actual package source directory.");
        var url = relative[..^Path.GetExtension(relative).Length].Replace('\\', '/');
        if (url.Split('/').Any(part => !IsValidAssetLeafName(part))) throw new InvalidOperationException("The source move has an invalid asset URL.");
        var relocated = item.Clone(keepPackage: true, newLocation: project.Qualify(new UFile(url)), newAsset: item.Asset);
        if (!SamePath(relocated.FullPath.ToOSPath(), destination))
            throw new InvalidOperationException("The native source asset URL does not map to the provider's physical destination.");
        return new(entry, url);
    }
}
