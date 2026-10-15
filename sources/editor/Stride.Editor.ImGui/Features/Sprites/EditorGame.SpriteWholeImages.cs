using Stride.Assets.Sprite;
using Stride.Core.Mathematics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class SpriteWholeImageWork(AssetMetadataDocument owner, SpriteSettingFrame[] frames,
        SpriteSettingField[] fields, object?[] certificate, Guid? undo, Guid? redo, CancellationTokenSource cancellation,
        Task<((int Width, int Height)[] Next, (int Width, int Height)[] Previous, (string Path, long Length, DateTime Modified)[] Stamps)> task,
        IDisposable lease)
    {
        internal readonly AssetMetadataDocument Owner = owner;
        internal readonly SpriteSettingFrame[] Frames = frames;
        internal readonly SpriteSettingField[] Fields = fields;
        internal readonly object?[] Certificate = certificate;
        internal readonly Guid? Undo = undo, Redo = redo;
        internal readonly Task<((int Width, int Height)[] Next, (int Width, int Height)[] Previous, (string Path, long Length, DateTime Modified)[] Stamps)> Task = task;
        internal Exception? Failure;
        private bool cancellationReleased;
        internal void Release(bool cancel)
        {
            if (!cancellationReleased)
            {
                if (cancel) cancellation.Cancel();
                cancellation.Dispose(); cancellationReleased = true;
            }
            lease.Dispose();
        }
    }
    private SpriteWholeImageWork? spriteWholeImages;
    private bool HasPendingWholeSpriteImages => spriteWholeImages is { Failure: null };
    private void QueueWholeSpriteImages(AssetMetadataDocument document)
    {
        if (!document.CanEditSpriteSettings || document.SelectedSpriteIds.Count == 0) return;
        if (spriteWholeImages is { Failure: null }) return;
        StopWholeSpriteImages();
        ValidatePendingPropertyStrings();
        if (IsAuthoringPipelineUpdating) throw new InvalidOperationException("Another authored operation must finish first.");
        document.CommitEdits();
        var frames = document.SelectedSpriteIds.Select(id => document.SpriteSettingsFrames.First(frame => frame.Id == id)).ToArray();
        var regions = frames.Select(frame => frame.Fields.First(field => field.Node.Name == nameof(SpriteInfo.TextureRegion))).ToArray();
        if (regions.Any(field => !document.CanEditSpriteSetting(field))) throw new InvalidOperationException("The selected sprite regions cannot be edited.");
        var sources = frames.Select(frame => frame.Fields.First(field => field.Node.Name == nameof(SpriteInfo.Source))).ToArray();
        var fields = sources.Concat(regions).ToArray();
        var certificate = ReadSpriteSourceCertificate(document, fields);
        var lease = AcquireSpriteSourceCertificate(document, fields);
        var cancellation = new CancellationTokenSource();
        try
        {
            var paths = sources.Select(field => field.Node.Retrieve()?.ToString() ?? "").ToArray();
            var task = DecodeSpriteSources(Path.GetDirectoryName(document.Path)!, paths, paths, cancellation.Token);
            spriteWholeImages = new(document, frames, fields, certificate, document.SessionHistory?.Stack.NextUndoTransaction?.Id,
                document.SessionHistory?.Stack.NextRedoTransaction?.Id, cancellation, task, lease);
        }
        catch (Exception failure)
        {
            cancellation.Dispose();
            try { lease.Dispose(); }
            catch (Exception release) { packageCommandCleanup.Add(lease.Dispose); throw new AggregateException(failure, release); }
            throw;
        }
    }
    private void PumpWholeSpriteImages()
    {
        if (spriteWholeImages is not { Failure: null, Task.IsCompleted: true } work) return;
        try
        {
            var result = work.Task.GetAwaiter().GetResult(); var owner = work.Owner;
            if (!SpriteSourceFieldsCurrent(owner, work.Fields) || !work.Certificate.SequenceEqual(ReadSpriteSourceCertificate(owner, work.Fields))
                || owner.SessionHistory?.Stack.NextUndoTransaction?.Id != work.Undo || owner.SessionHistory?.Stack.NextRedoTransaction?.Id != work.Redo)
                throw new InvalidOperationException("The selected sprites changed before the whole-image regions could be applied.");
            foreach (var stamp in result.Stamps)
            {
                var file = new FileInfo(stamp.Path);
                if ((file.Exists ? file.Length : -1) != stamp.Length || (file.Exists ? file.LastWriteTimeUtc : DateTime.MinValue) != stamp.Modified)
                    throw new IOException("The sprite source file changed while decoding.");
            }
            var history = owner.SessionHistory ?? throw new InvalidOperationException("The sprite history owner changed.");
            using (var captured = history.CaptureOperationsAtomic(() =>
            {
                for (var index = 0; index < work.Frames.Length; index++)
                {
                    var frame = owner.SpriteSettingsFrames.First(candidate => candidate.Id == work.Frames[index].Id);
                    var size = result.Next[index];
                    owner.SetSpriteFrameRegion(frame, new Rectangle(0, 0, size.Width, size.Height));
                }
            })) captured.Publish(history, "Use whole sprite");
            spriteWholeImages = null; ReleaseWholeSpriteImageOwner(work, false);
        }
        catch (Exception failure)
        {
            if (ReferenceEquals(spriteWholeImages, work)) work.Failure = failure;
            Execute(() => throw failure);
        }
    }
    private void ReleaseWholeSpriteImageOwner(SpriteWholeImageWork work, bool cancel)
    {
        try { work.Release(cancel); }
        catch { packageCommandCleanup.Add(() => work.Release(cancel)); throw; }
    }
    private void StopWholeSpriteImages()
    {
        if (spriteWholeImages is not { } work) return;
        // Detach actor publication first. The decoder owns immutable paths and CPU resources only.
        spriteWholeImages = null;
        _ = work.Task.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        ReleaseWholeSpriteImageOwner(work, true);
    }
}
