using Stride.Core.Assets;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed class AssetTagsOperation(Asset asset, string[] before, string[] after, Action undoRevision, Action redoRevision) : Operation
{
    internal bool Cancelled { get; set; }
    internal static void Apply(Asset asset, IEnumerable<string> tags)
    {
        asset.Tags.Clear();
        foreach (var tag in tags) asset.Tags.Add(tag);
    }
    protected override void Rollback() { if (!Cancelled) { Apply(asset, before); undoRevision(); } }
    protected override void Rollforward() { if (!Cancelled) { Apply(asset, after); redoRevision(); } }
}
