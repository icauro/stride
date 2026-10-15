using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal AssetMetadataDocument CreateRecoveryGeneration(EditorRecoveryRecord record, LoggerResult log)
    {
        using var stream = new MemoryStream(record.Payload, false);
        var restored = AssetFileSerializer.Load<Asset>(stream, new UFile(Path), log, record.Namespace);
        if (restored.Asset.Id.ToString() != record.AssetId || restored.Asset.Id != AuthoredAsset.Id || restored.Asset.GetType() != AuthoredAsset.GetType())
            throw new InvalidOperationException("The recovery snapshot changed its canonical asset identity or type.");
        var next = CreateLoadedGeneration(Path, restored, record.BaselineHash, SessionHistory, isReadOnly);
        next.savedRevision = -1; next.revision = next.nextRevision = 1;
        next.savedTags = savedTags.ToArray();
        next.CopyGameSettingsOrientationProvenanceFrom(this);
        return next;
    }
}
