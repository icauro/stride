namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private static void CommitSpriteMutation(EditorDeferredAssetMutation mutation, string title, Action release)
    {
        try { mutation.Commit(title); }
        catch (Exception failure)
        {
            try { mutation.Rollback(); mutation.Dispose(); release(); }
            catch (Exception recovery)
            { throw new AggregateException("Sprite changes and restoration failed.", failure, recovery); }
            throw;
        }
        // Failed cleanup retains the active journal/owner so the next commit or
        // cancel can finish the remaining cleanup phase before disposing graphs.
        mutation.Dispose(); release();
    }
}
