using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

/// <summary>Saved revision ownership for the actual package metadata touched by a domain operation.</summary>
internal sealed record PackageMetadataRevision(EditorAuthoringState<PackageMetadataDocument> State, long Before, long After)
{
    public void Apply(bool forward) => State.Document.RestoreRevision(forward ? After : Before);
    public static PackageMetadataRevision[] Prepare(EditorSessionHistory? history, ProjectWorkspace workspace, IEnumerable<Package> packages)
    {
        if (history == null) return [];
        return packages.Distinct().Select(package =>
        {
            var state = history.Registry.GetOrCreate(package.FullPath.ToOSPath(), null,
                () => new PackageMetadataDocument(workspace, package, history));
            return new PackageMetadataRevision(state, state.Document.Revision, state.Document.AllocateRevision());
        }).ToArray();
    }
}
