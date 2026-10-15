namespace Stride.Editor.ImGui;

internal partial class ScriptSourceDocument
{
    internal static ScriptSourceDocument CreateNewRecoverySource(EditorRecoveryRecord record, Stride.Core.Assets.SourceCodeAsset asset, EditorSessionHistory history)
    {
        var decoded = DetectEncoding(record.Payload);
        var text = decoded.Encoding.GetString(record.Payload, decoded.Preamble.Length, record.Payload.Length - decoded.Preamble.Length);
        return new AssetScriptSourceDocument(record.Path, asset, text, decoded.Encoding, history);
    }
    internal byte[] CaptureRecoveryBytes()
    {
        var content = encoding.GetBytes(Text); var result = new byte[preamble.Length + content.Length];
        preamble.CopyTo(result, 0); content.CopyTo(result, preamble.Length); return result;
    }
    internal ScriptSourceDocument CreateRecoveryGeneration(EditorRecoveryRecord record)
    {
        if (GeneratedByAssetId != null) throw new InvalidOperationException("Generated sources require recovery of their authored producer.");
        var next = sourceItem != null ? new AssetScriptSourceDocument(sourceItem, SessionHistory) : new ScriptSourceDocument(Path, !sourceCanSave, SessionHistory);
        var decoded = DetectEncoding(record.Payload);
        var text = decoded.Encoding.GetString(record.Payload, decoded.Preamble.Length, record.Payload.Length - decoded.Preamble.Length);
        if (text.Contains('\0')) { next.Dispose(); throw new InvalidDataException("The recovery source contains null characters."); }
        next.encoding = decoded.Encoding; next.preamble = decoded.Preamble;
        next.Text = text; next.EditorText = Normalize(text);
        next.newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : text.Contains('\r') ? "\r" : "\n";
        next.diskHash = record.BaselineHash.Length == 0 ? null : record.BaselineHash.ToArray();
        next.savedRevision = -1; next.revision = next.nextRevision = 1; next.Version++;
        if (record.ProjectPath != null) next.SetProjectOwnership(record.ProjectPath, folders: record.SourceFolders);
        return next;
    }
}
