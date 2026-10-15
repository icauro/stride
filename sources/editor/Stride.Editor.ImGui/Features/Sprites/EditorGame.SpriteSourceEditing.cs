using Stride.Core.IO;
using Stride.Core.Assets.Quantum;
using Stride.TextureConverter;
using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record SpriteSourceEdit(AssetMetadataDocument Document, SpriteSettingField[] Fields, UFile?[] Sources,
        long Version, long WatchRevision, string Path, Guid? UndoBoundary, Guid? RedoBoundary, bool ResetToBase, CancellationTokenSource Cancellation,
        Task<((int Width, int Height)[] Next, (int Width, int Height)[] Previous, (string Path, long Length, DateTime Modified)[] Stamps)> Task,
        object?[] Certificate, object?[]? BaseCertificate, IDisposable OwnerLease, string OperationName, INodePresenter[]? ProjectionTargets, object?[]? ProjectionCertificate)
    {
        internal UFile? SourceFor(SpriteSettingField field) => Sources[Array.FindIndex(Fields, candidate => ReferenceEquals(candidate.Node, field.Node))];
        internal Exception? Failure;
        private bool cancellationReleased;
        internal void ReleaseCancellation(bool cancel)
        {
            if (cancellationReleased) return;
            if (cancel) Cancellation.Cancel();
            Cancellation.Dispose(); cancellationReleased = true;
        }
    }
    private readonly Dictionary<AssetMetadataDocument, SpriteSourceEdit> spriteSourceEdits = [];
    private bool HasPendingSpriteSourceEdits => HasPendingWholeSpriteImages || spriteSourceEdits.Values.Any(edit => edit.Failure == null);
    private void DrawSpriteSourceSetting(AssetMetadataDocument document, SpriteSettingField member)
    {
        DrawSpritePropertyLabel(document, member);
        DrawRetainedSpriteSource(document, [member]);
    }
    private void QueueSpriteSourceEdit(AssetMetadataDocument document, SpriteSettingField[] members, UFile source, bool resetToBase = false)
        => QueueSpriteSourceEdit(document, members, Enumerable.Repeat<UFile?>(source, members.Length).ToArray(), resetToBase);
    private static object?[] ReadSpriteSourceBases(SpriteSettingField[] fields)
        => fields.SelectMany(field => new object?[] { ((IAssetNode)field.Node).BaseNode,
            ((IAssetNode)field.Node).BaseNode is Stride.Core.Quantum.IMemberNode inherited ? inherited.Retrieve() : null }).ToArray();
    private void QueueSpriteSourceEdit(AssetMetadataDocument document, SpriteSettingField[] members, UFile?[] sources, bool resetToBase = false, string operationName = "Edit sprite source", IReadOnlyList<INodePresenter>? projections = null)
    {
        UseTextureSettingsCommandContext();
        if (members.Length == 0 || members.Length != sources.Length) throw new ArgumentException("Invalid sprite source targets.");
        if (sources.Any(source => source?.ToString().IndexOfAny(['\0', '\r', '\n']) >= 0)) throw new ArgumentException("Invalid source path.");
        if (IsAuthoringPipelineUpdating) throw new InvalidOperationException("Another authored operation must finish before changing the sprite source.");
        document.CommitEdits();
        if (!SpriteSourceFieldsCurrent(document, members) || members.Any(member => !document.CanEditSpriteSetting(member)))
            throw new InvalidOperationException("This sprite source cannot be edited.");
        if (resetToBase && members.Select((field, index) => document.CanResetAssetSettingNode(field.Node)
            && ((IAssetNode)field.Node).BaseNode is Stride.Core.Quantum.IMemberNode inherited
            && Equals(inherited.Retrieve(), sources[index])).Any(valid => !valid))
            throw new InvalidOperationException("The sprite source base value changed.");
        ReleaseSpriteSourceEdit(document);
        sources = sources.ToArray();
        var chosen = sources.Select(source => source?.ToString() ?? "").ToArray();
        var certificate = ReadSpriteSourceCertificate(document, members);
        var baseCertificate = resetToBase ? ReadSpriteSourceBases(members) : null;
        var projectionTargets = projections?.ToArray();
        if (projectionTargets != null && (projectionTargets.Length != members.Length || !document.IsCurrentSpritePresenterGroup(projectionTargets)
            || projectionTargets.Select((node, index) => node.Type == typeof(UFile)
                && ReferenceEquals(node.GetNodeAccessor().Node, members[index].Node)).Any(valid => !valid)))
            throw new InvalidOperationException("The sprite source projection changed.");
        var projectionCertificate = projectionTargets == null ? null : document.CaptureSpritePresenterGroup(projectionTargets);
        var ownerLease = projectionTargets == null ? AcquireSpriteSourceCertificate(document, members) : AcquireSpritePresenterGroup(document, projectionTargets);
        CancellationTokenSource? decoderCancellation = null; Task? decoderTask = null;
        try
        {
        var parent = Path.GetDirectoryName(document.Path)!;
        var previous = members.Select(member => member.Node.Retrieve()?.ToString() ?? "").ToArray();
        var cancellation = decoderCancellation = new CancellationTokenSource(); var token = cancellation.Token;
        var task = DecodeSpriteSources(parent, chosen, previous, token);
        decoderTask = task;
        spriteSourceEdits.Add(document, new(document, members.ToArray(), sources, document.SpriteSettingsEditVersion,
            spriteSourceWatches.GetValueOrDefault(document)?.Revision ?? 0, document.Path,
            document.SessionHistory?.Stack.NextUndoTransaction?.Id, document.SessionHistory?.Stack.NextRedoTransaction?.Id, resetToBase, cancellation, task,
            certificate, baseCertificate, ownerLease, operationName, projectionTargets, projectionCertificate));
        }
        catch (Exception failure)
        {
            decoderCancellation?.Cancel(); decoderCancellation?.Dispose();
            if (decoderTask != null) _ = decoderTask.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            try { ownerLease.Dispose(); }
            catch (Exception release) { packageCommandCleanup.Add(ownerLease.Dispose); throw new AggregateException(failure, release); }
            throw;
        }
    }
    private static Task<((int Width, int Height)[] Next, (int Width, int Height)[] Previous,
        (string Path, long Length, DateTime Modified)[] Stamps)> DecodeSpriteSources(string parent, string[] chosen, string[] previous, CancellationToken token)
        => Task.Run(async () =>
        {
            await SpriteSourceDecodeSlots.WaitAsync(token);
            try
            {
                using var tool = new TextureTool(); var sizes = new Dictionary<string, (int Width, int Height)>(StringComparer.OrdinalIgnoreCase);
                var stamps = new List<(string Path, long Length, DateTime Modified)>();
                (int Width, int Height) Read(string value)
                {
                    token.ThrowIfCancellationRequested();
                    var path = string.IsNullOrEmpty(value) ? "" : Path.GetFullPath(value, parent);
                    if (sizes.TryGetValue(path, out var cached)) return cached;
                    if (path.Length == 0) { sizes.Add(path, (0, 0)); return (0, 0); }
                    var info = new FileInfo(path);
                    var stamp = (Path: path, Length: info.Exists ? info.Length : -1, Modified: info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue);
                    (int Width, int Height) size;
                    if (!info.Exists) size = (0, 0);
                    else { using var image = tool.Load(path, false); size = (image.Width, image.Height); }
                    stamps.Add(stamp); sizes.Add(path, size); return size;
                }
                var next = chosen.Select(Read).ToArray(); var old = previous.Select(Read).ToArray();
                return (next, old, stamps.ToArray());
            }
            finally { SpriteSourceDecodeSlots.Release(); }
        }, token);

    private void PumpSpriteSourceEdits()
    {
        PumpWholeSpriteImages();
        foreach (var edit in spriteSourceEdits.Values.ToArray())
        {
            if (edit.Failure != null || !edit.Task.IsCompleted) continue;
            try
            {
                var result = edit.Task.GetAwaiter().GetResult();
                if (!SpriteSourceFieldsCurrent(edit.Document, edit.Fields)
                    || !edit.Certificate.SequenceEqual(ReadSpriteSourceCertificate(edit.Document, edit.Fields))
                    || edit.BaseCertificate != null && !edit.BaseCertificate.SequenceEqual(ReadSpriteSourceBases(edit.Fields))
                    || edit.ProjectionTargets != null && (!edit.Document.IsCurrentSpritePresenterGroup(edit.ProjectionTargets)
                        || !edit.ProjectionCertificate!.SequenceEqual(edit.Document.CaptureSpritePresenterGroup(edit.ProjectionTargets)))
                    || edit.Document.Path != edit.Path || edit.Document.SpriteSettingsEditVersion != edit.Version
                    || (spriteSourceWatches.GetValueOrDefault(edit.Document)?.Revision ?? 0) != edit.WatchRevision
                    || edit.Document.SessionHistory?.Stack.NextUndoTransaction?.Id != edit.UndoBoundary
                    || edit.Document.SessionHistory?.Stack.NextRedoTransaction?.Id != edit.RedoBoundary)
                    throw new InvalidOperationException("The sprite source changed before the decoded assignment could be applied.");
                foreach (var stamp in result.Stamps)
                {
                    var info = new FileInfo(stamp.Path);
                    if ((info.Exists ? info.Length : -1) != stamp.Length || (info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue) != stamp.Modified)
                        throw new IOException("The sprite source file changed while decoding.");
                }
                edit.Document.ApplySpriteSourceChanges(edit.Fields, edit.Sources, result.Next, result.Previous, edit.ResetToBase, edit.OperationName, edit.ProjectionTargets);
                // Actual native journal completion is the acceptance boundary, before releasing any owner pins.
                spriteSourceEdits.Remove(edit.Document);
                ReleaseSpriteSourceOwner(edit, false);
            }
            catch (Exception failure)
            {
                if (spriteSourceEdits.TryGetValue(edit.Document, out var current) && ReferenceEquals(current, edit))
                {
                    edit.Failure = failure; edit.ReleaseCancellation(false);
                    // Keep the requested path/certificate and actual owner/provider pins for retry or cancellation.
                }
                Execute(() => throw failure);
            }
        }
    }
    private void ReleaseSpriteSourceEdit(AssetMetadataDocument document)
    {
        if (ReferenceEquals(spriteWholeImages?.Owner, document)) StopWholeSpriteImages();
        if (!spriteSourceEdits.Remove(document, out var edit)) return;
        // Detach actor publication before releasing graph/provider pins. The worker holds immutable paths and its own CPU decoder only.
        _ = edit.Task.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        ReleaseSpriteSourceOwner(edit, true);
    }
    private void ReleaseSpriteSourceOwner(SpriteSourceEdit edit, bool cancel)
    {
        void Release() { edit.ReleaseCancellation(cancel); edit.OwnerLease.Dispose(); }
        try { Release(); }
        catch { packageCommandCleanup.Add(Release); throw; }
    }
    private void ValidateSpriteSourceAssignments()
    {
        if (spriteSourceEdits.Count != 0 || spriteWholeImages != null)
            throw new InvalidOperationException("The sprite source assignment must finish or be canceled before continuing.",
                spriteSourceEdits.Values.FirstOrDefault(edit => edit.Failure != null)?.Failure ?? spriteWholeImages?.Failure);
    }
    private void ClearSpriteSourceEdits()
    { StopWholeSpriteImages(); foreach (var document in spriteSourceEdits.Keys.ToArray()) ReleaseSpriteSourceEdit(document); }
}
