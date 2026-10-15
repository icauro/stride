using Stride.Assets.Models;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed partial class ProceduralAssetDocument
{
    internal ProceduralAssetDocument CreateRecoveryGeneration(EditorRecoveryRecord record, LoggerResult log)
    {
        using var stream = new MemoryStream(record.Payload, false);
        var restored = AssetFileSerializer.Load<ProceduralModelAsset>(stream, new UFile(Path), log, record.Namespace);
        if (restored.Asset.Id.ToString() != record.AssetId || restored.Asset.Id != Asset.Id) throw new InvalidOperationException("The recovery snapshot has another procedural asset owner.");
        var next = new ProceduralAssetDocument(Path, restored, record.BaselineHash, SessionHistory, isReadOnly);
        next.savedRevision = -1; next.revision = next.nextRevision = 1; next.savedAssetTags = SavedTags.ToArray();
        return next;
    }
}
