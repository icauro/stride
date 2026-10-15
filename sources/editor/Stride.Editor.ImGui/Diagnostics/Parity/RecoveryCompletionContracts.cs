using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Stride.Editor.ImGui;

/// <summary>Real private journal protocol fixtures. Produce in a child process; verify only after that actual owner exits.</summary>
internal static class RecoveryCompletionContracts
{
    private sealed record Receipt(string ProjectPath, int ProcessId, DateTime ProcessStartedUtc,
        string[] Cases, bool CompletedSessionSealed);
    private static readonly string[] Cases = ["completed-missing-pointer", "completed-corrupt-pointer", "corrupt-completion",
        "inconsistent-completion", "corrupt-completed-checkpoint", "older-active-without-completion"];
    internal static async Task ProduceAsync(string ownedRoot)
    {
        ownedRoot = Path.GetFullPath(ownedRoot);
        if (!Path.GetFileName(ownedRoot).StartsWith("recovery-completion-", StringComparison.Ordinal)
            || Directory.Exists(ownedRoot) || File.Exists(ownedRoot))
            throw new InvalidOperationException("Completion fixtures require a new owned recovery-completion-* directory.");
        Directory.CreateDirectory(ownedRoot);
        var project = Path.Combine(ownedRoot, "journal-protocol.sdpkg");
        var source = Path.Combine(ownedRoot, "source.cs");
        await File.WriteAllTextAsync(project, "Private journal protocol fixture; not a native asset compilation fixture.");
        await File.WriteAllTextAsync(source, "baseline source");
        var record = new EditorRecoveryRecord(source, "source", null, null, project, null,
            SHA256.HashData(await File.ReadAllBytesAsync(source)), Encoding.UTF8.GetBytes("recovered draft Ω"), [], null);
        var sealedSession = false;
        foreach (var id in Cases)
        {
            var journalRoot = Path.Combine(ownedRoot, id);
            var store = new EditorRecoveryStore(project, journalRoot);
            await store.CheckpointAsync([record], []);
            if (id == "older-active-without-completion") continue;
            // A prior valid checkpoint must still exist when the latest one is
            // corrupt, otherwise fallback rejection would be a vacuous test.
            await store.CheckpointAsync([record with { Payload = Encoding.UTF8.GetBytes("second recovered draft Ω") }], []);
            await store.CompleteAsync();
            var rejected = false;
            try { await store.CheckpointAsync([record], []); }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("A completed journal accepted another checkpoint.");
            sealedSession = true;
            var pointer = Directory.GetFiles(journalRoot, "session.json", SearchOption.AllDirectories).Single();
            var directory = Path.GetDirectoryName(pointer)!;
            var certificatePath = Path.Combine(directory, EditorRecoveryStore.CompletionFileName);
            if (id == "completed-corrupt-pointer") await File.WriteAllTextAsync(pointer, "{");
            else File.Delete(pointer); // Only the just-produced private fixture pointer.
            if (id == "corrupt-completion") await File.WriteAllTextAsync(certificatePath, "{");
            if (id == "inconsistent-completion")
            {
                var certificate = JsonSerializer.Deserialize<EditorRecoveryCompletion>(await File.ReadAllBytesAsync(certificatePath))!;
                await File.WriteAllBytesAsync(certificatePath, JsonSerializer.SerializeToUtf8Bytes(certificate with { SessionId = Guid.NewGuid() }));
            }
            if (id == "corrupt-completed-checkpoint")
            {
                var certificate = JsonSerializer.Deserialize<EditorRecoveryCompletion>(await File.ReadAllBytesAsync(certificatePath))!;
                await File.WriteAllTextAsync(Path.Combine(directory, certificate.CheckpointFile), "{");
            }
            if (id == "corrupt-completion")
            {
                // An invalid completed session must not hide a valid sibling.
                var sibling = new EditorRecoveryStore(project, journalRoot);
                await sibling.CheckpointAsync([record], []);
            }
        }
        using var owner = Process.GetCurrentProcess();
        await File.WriteAllBytesAsync(Path.Combine(ownedRoot, "producer.json"), JsonSerializer.SerializeToUtf8Bytes(
            new Receipt(project, owner.Id, owner.StartTime.ToUniversalTime(), Cases, sealedSession)));
    }
    internal static object Verify(string ownedRoot)
    {
        ownedRoot = Path.GetFullPath(ownedRoot);
        var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllBytes(Path.Combine(ownedRoot, "producer.json")))
            ?? throw new InvalidDataException("The actual child producer receipt is missing.");
        try
        {
            using var owner = Process.GetProcessById(receipt.ProcessId);
            if (!owner.HasExited && owner.StartTime.ToUniversalTime() == receipt.ProcessStartedUtc)
                throw new InvalidOperationException("Wait for the actual journal producer to exit; live-owner suppression cannot prove completion safety.");
        }
        catch (ArgumentException) { }
        var results = new List<object>();
        foreach (var id in Cases)
        {
            var store = new EditorRecoveryStore(receipt.ProjectPath, Path.Combine(ownedRoot, id));
            var candidates = store.Discover();
            var expected = id is "older-active-without-completion" or "corrupt-completion" ? 1 : 0;
            if (candidates.Count != expected) throw new InvalidOperationException(id + " recovered " + candidates.Count + " sessions; expected " + expected + ".");
            if (expected == 1)
            {
                var restored = store.Read(candidates[0], candidates[0].Manifest.Documents.Single());
                if (Encoding.UTF8.GetString(restored.Payload) != "recovered draft Ω")
                    throw new InvalidDataException("The real uncompleted sibling/legacy payload did not retain its Unicode bytes.");
            }
            results.Add(new { id, candidateCount = candidates.Count, passed = true });
        }
        if (!receipt.CompletedSessionSealed) throw new InvalidOperationException("The actual producer did not reject post-completion checkpoints.");
        return new { passed = true, cases = results, actualExitedOwner = new { receipt.ProcessId, receipt.ProcessStartedUtc },
            coverage = "Real private recovery journal protocol across separate producer/reader processes; not authored editor crash/layout restoration." };
    }
}
