using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal EntityHierarchyDocument CreateRecoveryGeneration(EditorRecoveryRecord record, LoggerResult log)
    {
        using var stream = new MemoryStream(record.Payload, false);
        var restored = AssetFileSerializer.Load<Asset>(stream, new UFile(Path), log, record.Namespace);
        if (restored.Asset.Id.ToString() != record.AssetId || restored.Asset.Id != Asset.Id || restored.Asset.GetType() != Asset.GetType())
            throw new InvalidOperationException("The recovery snapshot changed its canonical hierarchy identity or type.");
        EntityHierarchyDocument next = this is PrefabDocument ? new PrefabDocument(Path, restored, SessionHistory!, isReadOnly)
            : new SceneDocument(Path, restored, SessionHistory!, isReadOnly);
        next.diskHash = record.BaselineHash.Length == 0 ? null : record.BaselineHash.ToArray();
        next.savedRevision = -1; next.revision = next.nextRevision = 1;
        next.savedAssetTags = SavedTags.ToArray();
        next.RestoreSelection(record.Selection, record.PrimarySelection);
        return next;
    }
}
