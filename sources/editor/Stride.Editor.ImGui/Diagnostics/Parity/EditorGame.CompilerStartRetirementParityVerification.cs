using System.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed class CompilerStartRetirementParityRun
{
    internal required ActiveProjectParityRun Owners;
    internal readonly object Gate = new();
    internal readonly Exception Sentinel = new InvalidOperationException("Private native compiler callback failure");
    internal readonly CancellationTokenSource Cancellation = new();
    internal Task<string>? Task;
    internal Process? Process;
    internal DateTime ProcessStartedUtc;
    internal FileStream? Lock;
    internal string? Output;
    internal CompilerRunIdentity? Identity;
    internal Exception? Observed;
    internal int Phase;
    internal bool Retired;
    internal readonly List<object> Checks = [];
}

internal sealed partial class EditorGame
{
    private CompilerStartRetirementParityRun? compilerStartRetirementParity;

    internal CompilerStartRetirementParityRun BeginCompilerStartRetirementParityVerification()
    {
        if (compilerStartRetirementParity != null || !OperatingSystem.IsWindows())
            throw new InvalidOperationException("Native compiler startup retirement requires its independent private Windows case.");
        var owners = BeginActiveProjectParityVerification();
        // This case protects real editor/history/dirty/live-borrower generations.
        // Ordinary clean zero-ref cache eviction is not a mutation of those owners.
        owners.StateBaselines = owners.StateBaselines.Where(state => state.ProtectedOwner || state.Dirty || state.ViewReferences != 0).ToArray();
        return compilerStartRetirementParity = new() { Owners = owners };
    }

    private void StartCompilerRetirementFault(CompilerStartRetirementParityRun run, bool lockedLog)
    {
        var path = run.Owners.First.FullPath.ToOSPath();
        var cancellation = run.Cancellation.Token;
        run.Task = Task.Run(() => CompiledProjectBuild.Build(path, cancellation, state =>
        {
            if (state.ProcessId is not { } pid) return;
            lock (run.Gate)
            {
                if (run.Process != null) return;
                if (state.Phase != "Building project" || state.LogDirectory == null || state.CompilerRun == null
                    || state.CompilerRun.Source != "project:" + path)
                    throw new InvalidOperationException("The startup fault callback has another native compiler owner.");
                run.Process = Process.GetProcessById(pid);
                _ = run.Process.SafeHandle;
                run.ProcessStartedUtc = run.Process.StartTime.ToUniversalTime();
                run.Output = state.LogDirectory; run.Identity = state.CompilerRun;
                if (lockedLog)
                    run.Lock = new FileStream(Path.Combine(run.Output, "project.log"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                else throw run.Sentinel;
            }
        }), cancellation);
    }

    private static bool ObserveCompilerRetirementFault(CompilerStartRetirementParityRun run)
    {
        if (run.Task?.IsCompleted != true) return false;
        if (run.Observed == null)
        {
            try { _ = run.Task.GetAwaiter().GetResult(); }
            catch (Exception error) { run.Observed = error; }
            if (run.Observed == null) throw new InvalidOperationException("The native startup fault unexpectedly compiled successfully.");
        }
        lock (run.Gate)
        {
            if (run.Process == null || run.Output == null || run.Identity == null)
                throw new InvalidOperationException("No actual started compiler process was observed at the failure boundary.");
            return run.Process.HasExited && NativeCompilerProcessRetirement.TryDrainForOutput(run.Output);
        }
    }

    internal object? StepCompilerStartRetirementParityVerification(CompilerStartRetirementParityRun run)
    {
        AssertActiveProjectParityOwners(run.Owners);
        switch (run.Phase)
        {
            case 0: StartCompilerRetirementFault(run, lockedLog: false); run.Phase++; return null;
            case 1:
                if (!ObserveCompilerRetirementFault(run)) return null;
                if (!ReferenceEquals(run.Observed, run.Sentinel))
                    throw new InvalidOperationException("Native process retirement replaced the original callback exception identity.");
                if (Directory.Exists(run.Output)) return null;
                run.Checks.Add(new { contract = "actual-post-start-callback-failure", compiler = run.Identity,
                    process = run.Process!.Id, processStartedUtc = run.ProcessStartedUtc, processExited = true, sameExceptionIdentity = true,
                    nativeProducerRetired = true, outputDeleted = true,
                    archive = OwnedProjectBuildOutput.FailedOutputLogDirectory(run.Output!) });
                run.Process.Dispose(); run.Process = null; run.Output = null; run.Identity = null; run.Observed = null;
                run.Phase++; return null;
            case 2: StartCompilerRetirementFault(run, lockedLog: true); run.Phase++; return null;
            case 3:
                if (!ObserveCompilerRetirementFault(run)) return null;
                if (run.Observed is not IOException || !Directory.Exists(run.Output) || run.Lock == null)
                    throw new InvalidOperationException("The real private writer-open collision did not retain its output after native process exit.");
                var retained = OwnedProjectBuildOutput.CaptureFailedOutputRetirements().SingleOrDefault(receipt =>
                    receipt.GetType().GetProperty("output")?.GetValue(receipt) is string output && SamePath(output, run.Output!));
                if (retained == null)
                    throw new InvalidOperationException("The exact locked failed compiler output lost retry ownership.");
                run.Checks.Add(new { contract = "actual-native-log-open-failure", process = run.Process!.Id, processStartedUtc = run.ProcessStartedUtc,
                    compiler = run.Identity, exceptionType = run.Observed.GetType().FullName,
                    processExited = true, nativeProducerRetired = true, outputRetainedWhileLocked = true, retirement = retained });
                run.Lock.Dispose(); run.Lock = null; run.Phase++; return null;
            case 4:
                if (!NativeCompilerProcessRetirement.TryDrainForOutput(run.Output!) || Directory.Exists(run.Output)) return null;
                var archive = OwnedProjectBuildOutput.FailedOutputLogDirectory(run.Output!);
                if (!File.Exists(Path.Combine(archive, "project.log")))
                    throw new InvalidOperationException("The released native writer-open fixture was deleted without preserving its diagnostic file.");
                run.Checks.Add(new { contract = "real-locked-output-retry", outputDeleted = true, archive,
                    boundary = "The private zero-byte collision file is retained; this is not a claim of full compiler log contents." });
                if (!RetireCompilerStartRetirementParityVerification(run)) return null;
                AssertActiveProjectParityOwners(run.Owners);
                return new { passed = true, checks = run.Checks.ToArray(), ownerReceipt = run.Owners.LastOwnerReceipt,
                    scope = "Real SDK child starts, original callback failure identity, real Windows native log-open failure, exact process/readers/output retry ownership; no fabricated task, project reopen or diagnostic output deletion." };
            default: throw new InvalidOperationException("Unknown native startup-retirement case phase.");
        }
    }

    internal bool RetireCompilerStartRetirementParityVerification(CompilerStartRetirementParityRun run)
    {
        if (run.Retired) return true;
        if (run.Task?.IsCompleted == false) { run.Cancellation.Cancel(); return false; }
        if (run.Task?.IsFaulted == true) _ = run.Task.Exception;
        lock (run.Gate)
        {
            run.Lock?.Dispose(); run.Lock = null;
            if (run.Process != null && !run.Process.HasExited) return false;
            if (run.Output != null && (!NativeCompilerProcessRetirement.TryDrainForOutput(run.Output) || Directory.Exists(run.Output))) return false;
            run.Process?.Dispose(); run.Process = null;
        }
        if (!RetireActiveProjectParityVerification(run.Owners)) return false;
        run.Cancellation.Dispose(); run.Retired = true;
        if (ReferenceEquals(compilerStartRetirementParity, run)) compilerStartRetirementParity = null;
        return true;
    }

    internal bool TryDrainCompilerStartRetirementParityVerification()
        => compilerStartRetirementParity == null || RetireCompilerStartRetirementParityVerification(compilerStartRetirementParity);
}
