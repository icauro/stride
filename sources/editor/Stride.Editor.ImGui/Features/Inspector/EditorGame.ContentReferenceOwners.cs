namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private IEditorDocument[] contentReferenceOwners = [];
    private sealed record ContentReferenceResources(Func<bool> Current, Func<IDisposable> Acquire, Action Rebaseline);
    private ContentReferenceResources[] contentReferenceResources = [];
    private IDisposable BeginContentReferenceResources(Func<bool> current, Func<IDisposable> acquire, Action rebaseline)
    {
        var previous = contentReferenceResources;
        contentReferenceResources = [.. previous, new(current, acquire, rebaseline)];
        return new ContentReferenceResourceScope(this, previous);
    }
    private sealed class ContentReferenceResourceScope(EditorGame editor, ContentReferenceResources[] previous) : IDisposable
    {
        private EditorGame? owner = editor;
        public void Dispose()
        { if (owner == null) return; owner.contentReferenceResources = previous; previous = []; owner = null; }
    }
    private IDisposable BeginContentReferenceOwnerScope(params IEditorDocument[] owners)
    {
        var previous = contentReferenceOwners;
        contentReferenceOwners = owners.Distinct().ToArray();
        return new ContentReferenceOwnerScope(this, previous);
    }
    private sealed class ContentReferenceOwnerScope(EditorGame editor, IEditorDocument[] previous) : IDisposable
    {
        private EditorGame? owner = editor;
        public void Dispose()
        {
            if (owner == null) return;
            owner.contentReferenceOwners = previous; previous = []; owner = null;
        }
    }
    private static long? ContentReferenceOwnerRevision(IEditorDocument document) => document switch
    {
        IAssetTagDocument asset => asset.EditRevision,
        PackageSettingsDocument settings => settings.AuthoringRevision,
        ProjectBuildSettingsDocument project => project.AuthoringRevision,
        PackageMetadataDocument package => package.Revision,
        _ => null,
    };
}
