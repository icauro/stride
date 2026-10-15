using System.Security.Cryptography;
using Stride.Assets.Models;
using Stride.Core.Assets;
using Stride.Core.IO;
using Stride.Rendering.ProceduralModels;

namespace Stride.Editor.ImGui;

// Owned-file primitive scope: no package relocation or shared history/UI claim.
internal static class AssetCrossVolumeParityVerification
{
    private sealed record Times(DateTime Creation, DateTime Access, DateTime Modified);
    internal static object Verify(string artifacts)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Actual Windows volumes are required.");
        var token = Guid.NewGuid().ToString("N");
        var sourceRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI", "cross-volume-" + token));
        var targetRoot = Path.GetFullPath(Path.Combine(artifacts, "asset-cross-volume", token));
        if (Directory.Exists(sourceRoot) || Directory.Exists(targetRoot)) throw new IOException("Private transfer fixture already exists.");
        Directory.CreateDirectory(sourceRoot); Directory.CreateDirectory(targetRoot);
        var source = Path.Combine(sourceRoot, "Authored-\u96ea.sdpromodel");
        var target = Path.Combine(targetRoot, "Authored-\u96ea.sdpromodel");
        var occupied = Path.Combine(targetRoot, "Occupied.sdpromodel");
        var probe = Path.Combine(targetRoot, "volume.bin");
        var ro = Path.Combine(sourceRoot, "ReadOnly.sdpromodel");
        var roTarget = Path.Combine(targetRoot, "ReadOnly.sdpromodel");
        var asset = new ProceduralModelAsset { Type = new CubeProceduralModel() };
        using var serialized = new MemoryStream(); AssetFileSerializer.Save(serialized, asset, null);
        var bytes = serialized.ToArray(); var hash = SHA256.HashData(bytes);
        var blocker = System.Text.Encoding.UTF8.GetBytes("Independent destination owner \u96ea");
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var receipts = new List<object>(); Exception? failure = null;
        Directory.CreateDirectory(artifacts);
        void Record(object receipt)
        {
            receipts.Add(receipt);
            // Persist native boundary evidence before any subsequent assertion or
            // cleanup can fail; receipt serialization cannot mask that failure.
            File.AppendAllText(Path.Combine(artifacts, "asset-cross-volume-boundaries.jsonl"),
                System.Text.Json.JsonSerializer.Serialize(receipt) + Environment.NewLine);
        }
        void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        void Create(string path, byte[] contents)
        { WindowsOwnedAssetFileWriter.Write(path, null, contents); files[path] = SHA256.HashData(contents); }
        WindowsSourceFileIdentity.Identity Identity(string path)
        { using var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return WindowsSourceFileIdentity.Capture(read); }
        byte[] ReadBytes(string path)
        {
            // Transfer retains READ/WRITE/DELETE on its destination. This observer
            // requests only READ, permits the existing handle, and closes before
            // the next move. The producer still rejects external writers/deleters.
            using var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var contents = new MemoryStream(); read.CopyTo(contents); return contents.ToArray();
        }
        Times Clock(string path) => new(File.GetCreationTimeUtc(path), File.GetLastAccessTimeUtc(path), File.GetLastWriteTimeUtc(path));
        void NativeAsset(string path)
        {
            var actual = ReadBytes(path);
            using var input = new MemoryStream(actual, false);
            var loaded = AssetFileSerializer.Load<ProceduralModelAsset>(input, new UFile(path)).Asset;
            Require(loaded.Id == asset.Id && loaded.Type is CubeProceduralModel && actual.SequenceEqual(bytes),
                "Transfer changed independently reloaded native YAML identity/type/bytes.");
        }
        void Delete(string path)
        {
            if (!File.Exists(path)) return;
            Require(files.TryGetValue(path, out var expected), "Cleanup cannot remove an unowned file.");
            OwnedFileIdentity identity;
            using (var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            { identity = WindowsOwnedAssetFileWriter.CaptureIdentity(read); Require(SHA256.HashData(read).SequenceEqual(expected), "Owned cleanup bytes changed."); }
            WindowsOwnedAssetFileDeletion.Delete(path, expected, true, identity);
        }
        try
        {
            Create(source, bytes); Create(probe, [1, 3, 5, 7]);
            var sourceVolume = Identity(source).Volume; var targetVolume = Identity(probe).Volume;
            Require(sourceVolume != targetVolume, "Source and target resolve to the same actual Windows volume.");
            Record(new { action = "Actual distinct Windows volumes", sourceVolume, targetVolume, sourceRoot, targetRoot }); Delete(probe);
            NativeAsset(source);
            File.SetCreationTimeUtc(source, new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(source, new DateTime(2024, 2, 4, 5, 6, 7, DateTimeKind.Utc));
            File.SetLastAccessTimeUtc(source, new DateTime(2024, 2, 5, 6, 7, 8, DateTimeKind.Utc));
            var initial = Identity(source); var times = Clock(source);
            var stale = hash.ToArray(); stale[0] ^= 1; var rejected = false;
            try { using var bad = new WindowsOwnedAssetFileRename(source, stale); } catch (IOException) { rejected = true; }
            Require(rejected && Identity(source) == initial && ReadBytes(source).SequenceEqual(bytes), "Stale SHA rejection changed the native source owner.");
            Record(new { action = "Rejected stale SHA", initial, sha = Convert.ToHexString(hash) });
            using (var move = new WindowsOwnedAssetFileRename(source, hash))
            {
                Record(new { action = "Retained source read boundary", initialTimes = times, afterRead = Clock(source), identity = Identity(source) });
                Create(occupied, blocker); var occupiedIdentity = Identity(occupied); rejected = false;
                try { move.Move(occupied); } catch (IOException) { rejected = true; }
                Require(rejected && move.CurrentPath == source && Identity(source) == initial && Identity(occupied) == occupiedIdentity
                    && ReadBytes(source).SequenceEqual(bytes) && ReadBytes(occupied).SequenceEqual(blocker), "Occupied destination rejection changed either native file.");
                Record(new { action = "Rejected occupied destination", initial, occupiedIdentity }); Delete(occupied);
                var previous = initial;
                foreach (var step in new[] { (Action: "forward", Path: target, Absent: source, Volume: targetVolume),
                    (Action: "reverse", Path: source, Absent: target, Volume: sourceVolume),
                    (Action: "redo", Path: target, Absent: source, Volume: targetVolume),
                    (Action: "final restore", Path: source, Absent: target, Volume: sourceVolume) })
                {
                    var sourcePathBefore = move.CurrentPath;
                    var sourceTimesBefore = Clock(sourcePathBefore);
                    move.Move(step.Path); files[step.Path] = hash;
                    var current = Identity(step.Path); var observedTimes = Clock(step.Path);
                    Require(move.CurrentPath == step.Path && !File.Exists(step.Absent) && current.Volume == step.Volume && current.Volume != previous.Volume,
                        "Transfer did not publish its owned object on the certified opposite volume and retire the source.");
                    Record(new { action = "Timestamp preservation boundary", transferAction = step.Action, sourcePathBefore, step.Path,
                        initialTimes = times, sourceTimesBefore, destinationTimes = observedTimes,
                        creationEqualsInitial = observedTimes.Creation == times.Creation,
                        accessEqualsInitial = observedTimes.Access == times.Access,
                        modifiedEqualsInitial = observedTimes.Modified == times.Modified,
                        equalsImmediateSource = observedTimes == sourceTimesBefore });
                    Require(observedTimes == sourceTimesBefore,
                        "Transfer did not preserve the exact actual source creation/access/modified timestamps at publication.");
                    Require(observedTimes.Creation == times.Creation && observedTimes.Modified == times.Modified,
                        "Transfer or diagnostic reads changed the original creation/modified timestamps.");
                    // Access time legitimately follows actual source SHA/native
                    // reads. Preserve its exact pre-transfer value, not the older
                    // fixture value from before those native reads occurred.
                    NativeAsset(step.Path);
                    Record(new { step.Action, step.Path, beforeIdentity = previous, afterIdentity = current, observedTimes, nativeAssetId = asset.Id.ToString(), sha = Convert.ToHexString(hash) }); previous = current;
                }
            }
            Create(ro, bytes); var attributes = File.GetAttributes(ro); var roIdentity = Identity(ro); Exception? error = null;
            try
            {
                File.SetAttributes(ro, attributes | FileAttributes.ReadOnly);
                try { using var move = new WindowsOwnedAssetFileRename(ro, hash); move.Move(roTarget); files[roTarget] = hash; }
                catch (IOException exception) { error = exception; }
                Require(error != null && !File.Exists(roTarget) && Identity(ro) == roIdentity && ReadBytes(ro).SequenceEqual(bytes),
                    "Actual read-only failure did not preserve the original object and remove its destination.");
                // Early native rejection is explicitly not post-copy compensation.
                Record(new { action = "Actual read-only failure restoration", error = error.ToString(), roIdentity,
                    postCopyCompensationExercised = error.Message == "The owned source file could not be removed after transfer.", destinationAbsent = !File.Exists(roTarget) });
            }
            finally { if (File.Exists(ro)) File.SetAttributes(ro, attributes); }
            Require(File.Exists(source) && !File.Exists(target), "Final reverse replay failed to restore the source path."); NativeAsset(source);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            var cleanup = new List<Exception>();
            foreach (var path in files.Keys) try { Delete(path); } catch (Exception error) { cleanup.Add(error); }
            foreach (var root in new[] { sourceRoot, targetRoot }) try { if (Directory.Exists(root)) Directory.Delete(root, false); } catch (Exception error) { cleanup.Add(error); }
            if (cleanup.Count != 0) { if (failure != null) cleanup.Insert(0, failure); failure = new AggregateException("Cross-volume verification and cleanup failed.", cleanup); }
        }
        var evidence = new { coverage = "Actual native Windows owned authored-file transfer on distinct certified volumes, native YAML reload, SHA/file identity/timestamps, guarded collision/stale input and reversible replay. No package membership, incoming references, shared history transaction, UI or full B41 claim.",
            sourceRoot, targetRoot, nativeAssetId = asset.Id.ToString(), receipts, failure = failure?.ToString() };
        try { File.WriteAllText(Path.Combine(artifacts, "asset-cross-volume-result.json"), System.Text.Json.JsonSerializer.Serialize(evidence)); }
        catch (Exception receiptFailure)
        {
            if (failure != null) throw new AggregateException("Native transfer verification failed and its final receipt could not be written.", failure, receiptFailure);
            throw;
        }
        if (failure != null) throw failure;
        return evidence;
    }
}
