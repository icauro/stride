using Stride.Core.Assets;
using Stride.Core.Assets.Templates;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Assets.Quantum.Templates;

namespace Stride.Editor.ImGui;

/// <summary>Actor preparation, real native worker and staged session publication.</summary>
internal sealed class RegisteredProjectTemplateRequest
{
    private enum Phase { Start, Preparing, Running, Releasing, Publishing, Complete }
    private readonly ProjectTemplateChoice choice;
    private readonly Guid descriptionId;
    private readonly string? descriptionPath;
    private readonly string name, location;
    private readonly ProjectSolutionSettings settings;
    private readonly Action<ILogMessage>? report;
    private readonly Action<Exception>? checkDeviceFailure;
    private readonly PackageSession? sourceSession;
    private readonly Func<PackageSession?>? currentSourceSession;
    private EventHandler<MessageLoggedEventArgs>? logHandler;
    private readonly CancellationTokenSource cancellation = new();
    private readonly EditorTemplatePreparationContext actor = new();
    private Phase phase;
    private PackageSession? session;
    private PackageSession? capturedSession;
    private ProjectTemplatePublication? publication;
    private SessionTemplateGeneratorParameters? parameters;
    private ITemplateGenerator<SessionTemplateGeneratorParameters>? original, generator;
    private QuantumGraphTemplateGenerator? ownedQuantum;
    private IEditorSessionTemplatePreparation? preparation;
    private Task? preparationDisposal;
    private EditorSessionTemplatePreparations.Lease? preparationLease;
    private Task<bool>? preparing;
    private Task<byte[]?>? running;
    private Task<string>? publishing;
    private byte[]? externalSolution;
    private string? solutionPath;
    private bool cancelled, sessionReleased, surfaceReleased, compensated, tokenReleased, solutionStaged, quantumReleased;
    private Exception? failure;
    private string? previousCleanupError;
    internal bool Complete => phase == Phase.Complete;
    internal bool IsCancelled => cancelled;
    internal string? Result { get; private set; }
    internal Exception? Failure => failure;

    internal RegisteredProjectTemplateRequest(ProjectTemplateChoice choice, string name, string location, ProjectSolutionSettings? settings,
        Action<ILogMessage>? report, Action<Exception>? checkDeviceFailure, PackageSession? sourceSession,
        Func<PackageSession?>? currentSourceSession)
    {
        this.choice = choice; descriptionId = choice.Description.Id; descriptionPath = choice.Description.FullPath?.ToOSPath();
        this.name = name; this.location = location; this.settings = settings ?? new("", ""); this.report = report; this.checkDeviceFailure = checkDeviceFailure;
        this.sourceSession = sourceSession; this.currentSourceSession = currentSourceSession;
    }

    internal void Cancel()
    {
        if (Complete) return;
        cancelled = true;
        if (!tokenReleased)
            try { cancellation.Cancel(); }
            catch (Exception error) { failure ??= error; checkDeviceFailure?.Invoke(error); }
    }
    internal void DrawPreparation()
    {
        if (phase != Phase.Preparing || cancelled || preparation == null) return;
        try { using var scope = actor.Enter(); preparation.Draw(); }
        catch (Exception error) { Fail(error); checkDeviceFailure?.Invoke(error); }
    }

    // Called only by the owner Update pump, outside nested GUI scopes. Retained
    // task/session/surface ownership is not detached on cancellation or failure.
    internal void Pump()
    {
        if (Complete) return;
        try
        {
            actor.Pump();
            if (phase == Phase.Start)
            {
                if (cancelled) { phase = Phase.Releasing; }
                else Start();
            }
            if (phase == Phase.Preparing)
            {
                if (preparing?.IsCompleted != true) return;
                if (actor.HasPendingWork) return;
                var accepted = preparing.GetAwaiter().GetResult(); preparing = null;
                if (!accepted)
                {
                    cancelled = true;
                    if (!parameters!.Logger.HasErrors) parameters.Logger.Info(EditorLocalization.Text("Operation cancelled.", "Log"));
                }
                if (parameters!.Logger.HasErrors) Fail(new InvalidOperationException(parameters.Logger.ToText()));
                if (cancelled) phase = Phase.Releasing;
                else
                {
                    ValidateOwner(); parameters.Unattended = true;
                    running = Task.Run(Run, CancellationToken.None); phase = Phase.Running;
                }
            }
            if (phase == Phase.Running)
            {
                if (running?.IsCompleted != true) return;
                externalSolution = running.GetAwaiter().GetResult(); running = null;
                phase = Phase.Releasing;
            }
        }
        catch (Exception error) { Fail(error); checkDeviceFailure?.Invoke(error); }
        if (phase == Phase.Releasing) Release();
        if (phase == Phase.Publishing && publishing!.IsCompleted)
        {
            try { Result = publishing.GetAwaiter().GetResult(); publishing = null; phase = Phase.Releasing; }
            catch (Exception error) { publishing = null; Fail(error); checkDeviceFailure?.Invoke(error); }
            Release();
        }
    }

    private void Start()
    {
        var destination = ProjectTemplateService.ValidateDestination(name, location);
        solutionPath = ProjectTemplateService.ValidateSolutionDestination(name, location, settings);
        original = TemplateManager.FindTemplateGenerator<SessionTemplateGeneratorParameters>(choice.Description)
            ?? throw new InvalidOperationException("Unable to retrieve template generator for the selected template. Aborting.");
        ValidateOwner();
        publication = new(destination);
        session = capturedSession = EditorPackageLoadPolicy.CreateSession();
        session.SolutionPath = new UFile(Path.Combine(publication.Stage, Path.GetFileName(solutionPath)));
        session.VisualStudioVersion = PackageSession.DefaultVisualStudioVersion;
        parameters = new()
        {
            Name = name, Namespace = name.Replace('-', '_'), Id = Guid.NewGuid(),
            OutputDirectory = new UDirectory(publication.Stage), Description = choice.Description,
            Session = session, Logger = new LoggerResult(), Unattended = false,
        };
        parameters.Validate();
        if (report != null)
        {
            logHandler = (_, args) => report(args.Message);
            parameters.Logger.MessageLogged += logHandler;
        }
        using (actor.Enter())
        {
            preparationLease = EditorSessionTemplatePreparations.Create(original, parameters, cancellation.Token);
            preparation = preparationLease?.Resource;
            generator = preparation == null ? original : preparation.Generator
                ?? throw new InvalidOperationException("The preparation adapter returned no native generator.");
            if (original is QuantumGraphTemplateGenerator originalQuantum)
            {
                var inner = preparation == null ? originalQuantum.Inner : generator;
                if (inner is QuantumGraphTemplateGenerator adaptedQuantum) inner = adaptedQuantum.Inner;
                if (inner is not SessionTemplateGenerator nativeInner)
                    throw new InvalidOperationException("The adapted generator must preserve the native SessionTemplateGenerator graph hooks.");
                generator = ownedQuantum = new QuantumGraphTemplateGenerator(nativeInner);
            }
            if (!generator.IsSupportingTemplate(choice.Description)) throw new InvalidOperationException("The preparation adapter does not support the selected template.");
            preparing = generator.PrepareForRun(parameters) ?? throw new InvalidOperationException("The generator returned no preparation task.");
        }
        phase = Phase.Preparing;
    }

    private byte[]? Run()
    {
        cancellation.Token.ThrowIfCancellationRequested(); ValidateOwner();
        if (!generator!.Run(parameters!) || parameters!.Logger.HasErrors)
            throw new InvalidOperationException(parameters.Logger.ToText());
        cancellation.Token.ThrowIfCancellationRequested(); ValidateOwner();
        // Serialize the generator's actual session/project graph. A generated
        // template solution file is not a replacement for native session state.
        var staged = Path.Combine(publication!.Stage, ".stride-native-solution-" + Guid.NewGuid().ToString("N") + ".slnx");
        session!.StageGeneratedSolution(staged, solutionPath!, publication.Stage, publication.Destination);
        solutionStaged = true;
        var bytes = File.ReadAllBytes(staged);
        var stageSolution = Path.Combine(publication.Stage, Path.GetFileName(solutionPath));
        if (File.Exists(stageSolution)) ProjectTemplateService.DeletePrivateOutput(stageSolution);
        ProjectTemplateService.DeletePrivateOutput(staged);
        if (ProjectTemplatePublication.Inside(publication.Destination, solutionPath!))
        {
            stageSolution = Path.Combine(publication.Stage, Path.GetRelativePath(publication.Destination, solutionPath!));
            var parent = publication.EnsureDirectory(Path.GetDirectoryName(stageSolution)!);
            WindowsOwnedAssetFileWriter.WriteNewInDirectory(stageSolution, parent, bytes);
            return null;
        }
        return bytes;
    }

    private void ValidateOwner()
    {
        if (choice.Description.Id != descriptionId || !string.Equals(choice.Description.FullPath?.ToOSPath(), descriptionPath, StringComparison.Ordinal))
            throw new OperationCanceledException("The selected native template identity changed.");
        if (parameters != null && (!ReferenceEquals(parameters.Session, capturedSession)
            || !ReferenceEquals(parameters.Description, choice.Description)
            || publication == null || !ProjectTemplatePublication.Inside(publication.Stage, parameters.OutputDirectory.ToOSPath())))
            throw new InvalidOperationException("The native template changed its owned session, description or output stage.");
        if (!solutionStaged && capturedSession?.SolutionPath is { } nativeSolution && publication != null
            && !ProjectTemplatePublication.Inside(publication.Stage, nativeSolution.ToOSPath()))
            throw new InvalidOperationException("The native template solution escaped its owned stage.");
        if (preparationLease?.IsUnloading == true || EditorExtensionGenerations.IsUnloading(choice.Description.GetType().Assembly)
            || original != null && EditorExtensionGenerations.IsUnloading(original.GetType().Assembly)
            || generator != null && EditorExtensionGenerations.IsUnloading(generator.GetType().Assembly)
            || original is QuantumGraphTemplateGenerator originalGraph && EditorExtensionGenerations.IsUnloading(originalGraph.Inner.GetType().Assembly)
            || ownedQuantum != null && EditorExtensionGenerations.IsUnloading(ownedQuantum.Inner.GetType().Assembly)
            || preparation != null && EditorExtensionGenerations.IsUnloading(preparation.GetType().Assembly))
            throw new OperationCanceledException("The native template generation is unloading.");
        if (actor.IsOwnerThread && currentSourceSession != null && !ReferenceEquals(sourceSession, currentSourceSession()))
            throw new OperationCanceledException("The source template session changed.");
        if (actor.IsOwnerThread && !TemplateManager.FindTemplates(TemplateScope.Session, sourceSession).Any(description => ReferenceEquals(description, choice.Description)))
            throw new OperationCanceledException("The selected native template is no longer registered.");
        if (original != null && (!TemplateManager.IsRegistered(original)
            || actor.IsOwnerThread && !ReferenceEquals(original, TemplateManager.FindTemplateGenerator<SessionTemplateGeneratorParameters>(choice.Description))))
            throw new OperationCanceledException("The selected template generator registration changed.");
    }
    private void Fail(Exception error)
    {
        failure ??= error; cancelled = true;
        try { cancellation.Cancel(); } catch (Exception cancelError) { failure = new AggregateException(failure, cancelError); }
        // A failed surface draw can leave genuine preparation work alive.
        if (preparing?.IsCompleted != false && running?.IsCompleted != false) phase = Phase.Releasing;
    }
    private void Release()
    {
        try
        {
            if (actor.HasPendingWork) return;
            if (!quantumReleased)
            {
                using (actor.Enter()) ownedQuantum?.Dispose();
                quantumReleased = true;
                if (actor.HasPendingWork) return;
            }
            if (!surfaceReleased)
            {
                if (preparation is IAsyncDisposable asyncPreparation)
                {
                    if (preparationDisposal == null)
                        using (actor.Enter()) preparationDisposal = asyncPreparation.DisposeAsync().AsTask();
                    if (!preparationDisposal.IsCompleted) return;
                    try { preparationDisposal.GetAwaiter().GetResult(); }
                    catch { preparationDisposal = null; throw; }
                }
                else using (actor.Enter()) preparation?.Dispose();
                preparation = null; surfaceReleased = true;
            }
            // Drain actual preparation/disposal callbacks before releasing their
            // canonical session. Close refuses pending callbacks without waiting.
            if (!actor.Close()) return;
            if (!sessionReleased) { session?.Dispose(); session = null; sessionReleased = true; }
            if (logHandler != null && parameters != null) { parameters.Logger.MessageLogged -= logHandler; logHandler = null; }
            if (Result == null && failure == null && !cancelled && publication != null)
            {
                try
                {
                    ValidateOwner();
                    publishing = Task.Run(() => publication.Publish(solutionPath!, externalSolution, cancellation.Token));
                    phase = Phase.Publishing; return;
                }
                catch (Exception error) { Fail(error); }
            }
            if (Result == null && publication != null && !compensated)
            {
                // Failed compensation retains a durable exact-ownership recovery
                // record. It is not reported as a successful output publication.
                compensated = true;
                try { publication.Compensate(failure ?? new OperationCanceledException()); }
                catch (Exception cleanup) { failure = failure == null ? cleanup : new AggregateException(failure, cleanup); }
            }
            if (!tokenReleased) { cancellation.Dispose(); tokenReleased = true; }
            phase = Phase.Complete;
        }
        catch (Exception cleanup)
        {
            var detail = cleanup.ToString();
            if (detail != previousCleanupError)
            {
                previousCleanupError = detail;
                EditorBackgroundJobRetirement.ReportFailure("Native template owner cleanup retained for retry", cleanup);
                report?.Invoke(new LogMessage("Templates", LogMessageType.Error, cleanup.Message, cleanup, null));
            }
            checkDeviceFailure?.Invoke(cleanup);
        }
    }
}
