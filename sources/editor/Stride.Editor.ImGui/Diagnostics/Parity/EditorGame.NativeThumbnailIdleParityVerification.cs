using System.Diagnostics;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Stride.Editor.Thumbnails;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private NativeThumbnailIdleVerificationOwner? nativeThumbnailIdleVerification;
    private sealed class NativeThumbnailIdleVerificationOwner
    {
        internal readonly NativeThumbnailCompilationSnapshot Capture;
        internal readonly IDisposable FixtureLease;
        internal readonly List<object> Progress = [];
        internal readonly NativeThumbnailResidentQueue Queue;
        internal Task? DrainTask;
        internal long RetryAt;
        internal string? DrainError;
        internal NativeThumbnailIdleVerificationOwner(NativeThumbnailCompilationSnapshot capture, IDisposable fixtureLease)
        {
            Capture = capture; FixtureLease = fixtureLease;
            Queue = new NativeThumbnailResidentQueue(state =>
            {
                lock (Progress)
                {
                    Progress.Add(new { state.Phase, state.Detail, state.ProcessId });
                    if (Progress.Count > 32) Progress.RemoveAt(0);
                }
            }, TimeSpan.FromSeconds(1));
        }
        internal async Task DrainAsync()
        {
            await Queue.DisposeAsync().ConfigureAwait(false);
            NativeThumbnailArtifactCleanup.DisposeAll([Capture, FixtureLease]);
        }
    }
    internal bool TryDrainNativeThumbnailIdleVerification()
    {
        var owner = nativeThumbnailIdleVerification;
        if (owner == null) return true;
        if (owner.DrainTask == null)
        {
            if (Environment.TickCount64 < owner.RetryAt) return false;
            owner.DrainTask = owner.DrainAsync();
        }
        if (!owner.DrainTask.IsCompleted) return false;
        if (!owner.DrainTask.IsCompletedSuccessfully)
        {
            owner.DrainError = owner.DrainTask.Exception?.ToString() ?? "Native thumbnail diagnostic drain canceled.";
            owner.DrainTask = null; owner.RetryAt = Environment.TickCount64 + 250;
            return false;
        }
        nativeThumbnailIdleVerification = null;
        return true;
    }
    internal string? NativeThumbnailIdleVerificationDrainError => nativeThumbnailIdleVerification?.DrainError;
    // Invoke on the editor ownership actor. All canonical graph capture happens
    // before handing immutable leased inputs to the asynchronous worker test.
    internal Task<object> VerifyNativeThumbnailIdleParityAsync(string ownedDirectory, CancellationToken cancellation = default)
    {
        if (nativeThumbnailIdleVerification != null)
            throw new InvalidOperationException("The previous thumbnail idle verification owner has not drained.");
        var privateRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (!Environment.GetCommandLineArgs().Contains("--ui-test") || workspace == null
            || !Path.GetFullPath(workspace.BuildProjectPath).StartsWith(privateRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Native thumbnail idle verification requires the loaded private UI fixture.");
        var entry = workspace.Assets.Single(asset => asset.Name == "Sphere" && asset.Extension == ".sdpromodel");
        BeginAssetThumbnails();
        var compiler = assetThumbnails?.GetNativeCompiler(CurrentThumbnailAsset(workspace.FindAsset(entry.Id)).GetType())
            ?? throw new InvalidOperationException("The canonical sphere has no actual registered native thumbnail compiler.");
        var capture = CaptureNativeThumbnail(entry, compiler.GetType());
        IDisposable? fixtureLease = null;
        try
        {
            fixtureLease = capture.AcquireArtifactLease();
            var root = Path.GetFullPath(ownedDirectory); Directory.CreateDirectory(root);
            if (File.Exists(Path.Combine(root, "first.png")) || File.Exists(Path.Combine(root, "restarted.png")))
                throw new IOException("Native thumbnail idle verification requires fresh owned artifacts.");
            var owner = new NativeThumbnailIdleVerificationOwner(capture, fixtureLease);
            nativeThumbnailIdleVerification = owner;
            return VerifyNativeThumbnailIdleImmutableAsync(owner, root, cancellation);
        }
        catch { fixtureLease?.Dispose(); capture.Dispose(); throw; }
    }

    private static async Task<object> VerifyNativeThumbnailIdleImmutableAsync(NativeThumbnailIdleVerificationOwner owner,
        string root, CancellationToken cancellation)
    {
        var capture = owner.Capture; var queue = owner.Queue; var progress = owner.Progress;
        try
        {
            var firstTask = queue.Submit(capture, 1, cancellation);
            // An explicit fixture lease remains for the restart. This does not
            // claim job-only ownership while the source wrapper is released.
            capture.Dispose();
            var first = await firstTask.WaitAsync(TimeSpan.FromSeconds(120), cancellation).ConfigureAwait(false);
            var firstImage = VerifyNativeIdlePng(first, capture, Path.Combine(root, "first.png"));
            var started = Lifecycle(queue);
            var firstPid = Number(started, "ownedWorkerProcessId");
            if (firstPid <= 0 || Number(started, "workerStartCount") != 1 || Bool(started, "closing"))
                throw new InvalidOperationException("First genuine thumbnail must have one live owned native worker.");
            using var firstProcess = Process.GetProcessById(firstPid);
            var firstCreated = firstProcess.StartTime.ToUniversalTime();
            var retired = await WaitNativeIdleState(queue, state => Number(state, "idleRetirementCount") >= 1
                && Number(state, "ownedWorkerProcessId") == 0 && !Bool(state, "idleRetirementPending"), cancellation).ConfigureAwait(false);
            if (!firstProcess.HasExited || Number(retired, "queuedCount") != 0 || Number(retired, "runningCount") != 0
                || retired.GetProperty("actorStatus").GetString() is "RanToCompletion" or "Faulted" or "Canceled")
                throw new InvalidOperationException("Idle retirement must exit the actual owned process and retain the live empty queue actor.");
            var restarted = await queue.Submit(capture, 1, cancellation)
                .WaitAsync(TimeSpan.FromSeconds(120), cancellation).ConfigureAwait(false);
            var secondImage = VerifyNativeIdlePng(restarted, capture, Path.Combine(root, "restarted.png"));
            var secondState = Lifecycle(queue);
            var secondPid = Number(secondState, "ownedWorkerProcessId");
            if (secondPid <= 0 || Number(secondState, "workerStartCount") != 2
                || Number(secondState, "idleRetirementCount") < 1)
                throw new InvalidOperationException("Second genuine thumbnail must start a fresh native worker after idle exit.");
            using var secondProcess = Process.GetProcessById(secondPid);
            var secondCreated = secondProcess.StartTime.ToUniversalTime();
            if (secondPid == firstPid && secondCreated == firstCreated)
                throw new InvalidOperationException("Restart reused the already retired process identity.");
            if (started.GetProperty("ownedWorkerGeneration").GetString()
                != secondState.GetProperty("ownedWorkerGeneration").GetString())
                throw new InvalidOperationException("Restart must use the same certified actual module generation.");
            await queue.DisposeAsync().ConfigureAwait(false);
            if (!secondProcess.HasExited) throw new InvalidOperationException("Final queue disposal did not exit its actual restarted worker.");
            object[] observations; lock (progress) observations = progress.ToArray();
            return new { assetId = capture.Authored.Parameters.RootId, capture.Identity, firstImage, secondImage,
                firstPid, firstCreated, secondPid, secondCreated, started, retired, secondState,
                actualIdleExit = true, actualRestartedRender = true, finalOwnedProcessExited = true,
                sourceWrapperReleasedAfterFirstSubmit = true, explicitFixtureArtifactLeaseRetainedAcrossRestart = true,
                observations, gaps = new[] { "Concurrent submission during retirement and canceled queued-job ownership are not asserted." } };
        }
        finally
        {
            // The EditorGame keeps this exact owner even when asynchronous
            // cleanup faults. Its terminal pump retries before certification.
            await owner.DrainAsync().ConfigureAwait(false);
        }
    }
    private static object VerifyNativeIdlePng(NativeThumbnailBatchResult result, NativeThumbnailCompilationSnapshot capture, string output)
    {
        var png = result.Png;
        if (result.Result != ThumbnailBuildResult.Succeeded || result.Error != null
            || result.AssetId != capture.Authored.Parameters.RootId || result.Identity != capture.Identity
            || png is not { Length: >= 24 } || png.Length > 1024 * 1024
            || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("The actual native worker did not return the exact canonical asset's successful bounded PNG.");
        var width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
            throw new InvalidDataException("Native thumbnail PNG dimensions are invalid.");
        File.WriteAllBytes(output, png);
        return new { output, width, height, bytes = png.Length, sha256 = Convert.ToHexString(SHA256.HashData(png)) };
    }
    private static JsonElement Lifecycle(NativeThumbnailResidentQueue queue)
        => JsonSerializer.SerializeToElement(queue.InspectLifecycle());
    private static int Number(JsonElement value, string name)
        => value.GetProperty(name).ValueKind == JsonValueKind.Null ? 0 : value.GetProperty(name).GetInt32();
    private static bool Bool(JsonElement value, string name) => value.GetProperty(name).GetBoolean();
    private static async Task<JsonElement> WaitNativeIdleState(NativeThumbnailResidentQueue queue,
        Func<JsonElement, bool> condition, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(20))
        {
            var state = Lifecycle(queue);
            if (condition(state)) return state;
            await Task.Delay(25, cancellation).ConfigureAwait(false);
        }
        throw new TimeoutException("Actual native worker did not finish bounded idle retirement: " + Lifecycle(queue));
    }
}
