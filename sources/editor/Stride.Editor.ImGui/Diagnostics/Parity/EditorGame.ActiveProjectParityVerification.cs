using System.Xml.Linq;
using Stride.Assets;
using Stride.Assets.Entities;
using Stride.Assets.Models;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Serializers;
using Stride.Core.IO;
using Stride.Core.Serialization;
using Stride.Engine;
using Stride.Rendering;
using Stride.Rendering.ProceduralModels;

namespace Stride.Editor.ImGui;

internal sealed record ActiveProjectParityFixture(string FirstProject, string SecondProject, string Solution,
    AssetId SceneId, AssetId ModelId, Guid MarkerEntity);

internal sealed record ActiveProjectStateBaseline(EditorAuthoringState State, Guid Generation, string Path, string Type,
    bool ProtectedOwner, bool NativeSettingsBorrower, bool Dirty, bool Published, int ViewReferences, int HistoryReferences);

internal sealed class ActiveProjectParityRun
{
    internal required ProjectWorkspace Workspace;
    internal required EditorSessionHistory History;
    internal required SolutionProject First, Second;
    internal required CompiledProjectContent BaselineContent;
    internal required EditorDocumentEntry[] Views;
    internal required EditorAuthoringState[] States;
    internal required Guid[] HistoryIds;
    internal required Dictionary<string, byte[]> AuthoredFiles;
    internal required IDisposable BaselineLease;
    internal required ActiveProjectParityFixture Fixture;
    internal EntityHierarchyDocument? Document;
    internal EngineViewport? Viewport;
    internal required EditorDocumentEntry ViewEntry;
    internal required EditorAuthoringState ViewState;
    internal required CompiledProjectContent SceneContent;
    internal required Guid[] RuntimeEntityIds;
    internal EditorDocumentEntry? ActiveEntry;
    internal object? LastOwnerReceipt;
    internal object? CertifiedCancelledCompilerRetirement;
    internal object? HistoryBefore;
    internal int HistoryPosition;
    internal ActiveProjectStateBaseline[] StateBaselines = [];
    internal Stride.Core.Mathematics.Matrix Camera;
    internal readonly List<Task<string>> Requests = [];
    internal readonly List<CompiledProjectContent> Adopted = [];
    internal readonly List<object> Checks = [];
    internal int Phase;
    internal bool CleanupStarted, LeaseReleased, Retired;
    internal Task<string>? CleanupReturn;
    internal CompiledProjectContent? ReturnedContent;
}

internal sealed partial class EditorGame
{
    private ActiveProjectParityFixture? activeProjectParityFixture;
    private ActiveProjectParityRun? activeProjectParityRun;

    internal ActiveProjectParityFixture PrepareActiveProjectParityFixture()
    {
        if (!args.Contains("--ui-test") || workspace != null || projectLoading != null || activeProjectParityFixture != null)
            throw new InvalidOperationException("Prepare the genuine switch solution once, before the private project load.");
        var first = Path.ChangeExtension(Path.GetFullPath(projectPathInput), ".csproj");
        var privateRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (!first.StartsWith(privateRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(first))
            throw new InvalidOperationException("Active-project fixtures require the actual privately copied SDK project.");
        var token = Guid.NewGuid().ToString("N");
        var directory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(first)!, "ActiveSwitch-" + token));
        if (!directory.StartsWith(privateRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The second native project must remain in the owned test root.");
        Directory.CreateDirectory(directory);
        var second = Path.Combine(directory, "NativeActiveSwitch.csproj");
        var nativeNuget = Path.Combine(Path.GetDirectoryName(first)!, "NuGet.Config");
        if (!File.Exists(nativeNuget)) throw new FileNotFoundException("The private fixture has no native NuGet source configuration.", nativeNuget);
        File.Copy(nativeNuget, Path.Combine(directory, "NuGet.Config"));
        File.WriteAllText(Path.Combine(directory, "ActualActiveSwitchMarker.cs"),
            "namespace ActiveSwitch" + token + "; public sealed class ActualActiveSwitchMarker { }\n");
        var project = XDocument.Load(first);
        foreach (var value in project.Descendants().Where(element => element.Name.LocalName == "RootNamespace"))
            value.Value = "ActiveSwitch" + token;
        project.Save(second);
        var firstProject = XDocument.Load(first);
        var xmlNamespace = firstProject.Root!.Name.Namespace;
        firstProject.Root.Add(new XElement(xmlNamespace + "ItemGroup",
            new XElement(xmlNamespace + "Compile", new XAttribute("Remove", "ActiveSwitch-" + token + "/**/*.cs"))));
        firstProject.Save(first);
        var central = Path.Combine(Path.GetDirectoryName(first)!, "Directory.Packages.props");
        if (File.Exists(central)) File.Copy(central, Path.Combine(directory, "Directory.Packages.props"));
        var assets = Path.Combine(directory, "Assets"); Directory.CreateDirectory(assets);
        var model = new ProceduralModelAsset { Type = new CubeProceduralModel() };
        var scene = new SceneAsset();
        var entity = new Entity("NativeActiveSwitchMarker");
        entity.Add(new ModelComponent { Model = AttachedReferenceManager.CreateProxyObject<Model>(model.Id, "SwitchModel") });
        scene.Hierarchy.Parts.Add(new EntityDesign(entity)); scene.Hierarchy.RootParts.Add(entity);
        var settings = GameSettingsFactory.Create();
        settings.DefaultScene = AttachedReferenceManager.CreateProxyObject<Scene>(scene.Id, "SwitchScene");
        AssetFileSerializer.Save(Path.Combine(assets, "SwitchModel.sdpromodel"), model, null);
        AssetFileSerializer.Save(Path.Combine(assets, "SwitchScene.sdscene"), scene, null);
        AssetFileSerializer.Save(Path.Combine(assets, "GameSettings.sdgamesettings"), settings, null);
        var package = new Package(); package.Meta.Name = "ActiveSwitch" + token;
        package.Meta.Version = new PackageVersion("1.0.0");
        package.AssetFolders.Add(new AssetFolder(new UDirectory("Assets")));
        var packageOwner = new StandalonePackage(package);
        package.RootAssets.Add(new AssetReference(model.Id, packageOwner.Qualify(new UFile("SwitchModel"))));
        package.RootAssets.Add(new AssetReference(scene.Id, packageOwner.Qualify(new UFile("SwitchScene"))));
        package.RootAssets.Add(new AssetReference(settings.Id, packageOwner.Qualify(new UFile("GameSettings"))));
        AssetFileSerializer.Save(Path.ChangeExtension(second, ".sdpkg"), package, null);
        var solutionPath = Path.Combine(Path.GetDirectoryName(first)!, "ActiveSwitchProof.sln");
        if (File.Exists(solutionPath)) throw new IOException("The private active-project solution already exists.");
        var firstId = Guid.NewGuid(); var secondId = Guid.NewGuid();
        var solution = new Stride.Core.Solutions.Solution { FullPath = solutionPath, StartupProjectGuid = firstId };
        solution.Projects.Add(new Stride.Core.Solutions.Project(firstId, Stride.Core.Solutions.KnownProjectTypeGuid.CSharp,
            Path.GetFileNameWithoutExtension(first), first, Guid.Empty, [], [], []));
        solution.Projects.Add(new Stride.Core.Solutions.Project(secondId, Stride.Core.Solutions.KnownProjectTypeGuid.CSharp,
            Path.GetFileNameWithoutExtension(second), second, Guid.Empty, [], [], []));
        solution.SaveAs(solutionPath);
        var reopened = Stride.Core.Solutions.Solution.FromFile(solutionPath);
        if (reopened.Projects.Count != 2 || !reopened.Projects.Any(item => SamePath(item.FullPath, second)))
            throw new InvalidDataException("Native solution serialization lost the actual second SDK project.");
        projectPathInput = solutionPath;
        return activeProjectParityFixture = new(first, second, solutionPath, scene.Id, model.Id, entity.Id);
    }

    internal ActiveProjectParityRun BeginActiveProjectParityVerification()
    {
        if (activeProjectParityRun != null) throw new InvalidOperationException("The previous native active-project case still owns its cleanup.");
        var fixture = activeProjectParityFixture ?? throw new InvalidOperationException("The actual two-project fixture was not prepared before load.");
        if (!args.Contains("--ui-test") || workspace == null || sessionHistory == null || projectContent == null
            || document == null || document.IsDirty || IsAuthoringPipelineUpdating || IsActiveProjectSwitching)
            throw new InvalidOperationException("The native switch case requires a clean, idle compiled scene owner.");
        var first = workspace.Projects.Single(item => SamePath(item.FullPath.ToOSPath(), fixture.FirstProject));
        var second = workspace.Projects.Single(item => SamePath(item.FullPath.ToOSPath(), fixture.SecondProject));
        if (!ReferenceEquals(workspace.Session.CurrentProject, first) || first.Package.State < PackageState.AssetsReady
            || second.Package.State < PackageState.AssetsReady)
            throw new InvalidOperationException("Both actual SDK projects must be natively loaded, with the original active.");
        var nativeViewport = InspectionViewport ?? throw new InvalidOperationException("The native canonical scene viewport is not ready.");
        var nativeView = sceneDocumentViews.Values.Single(view => ReferenceEquals(view.Viewport, nativeViewport));
        if (!IsCurrentSceneDocumentView(nativeView) || nativeViewport.RuntimeScene?.RootScene == null)
            throw new InvalidOperationException("The native scene view has no current canonical graph/content authority.");
        var run = new ActiveProjectParityRun { Workspace = workspace, History = sessionHistory, First = first, Second = second,
            Fixture = fixture, BaselineContent = projectContent,
            Views = documents.Entries.ToArray(), States = sessionHistory.Registry.PublishedStates.ToArray(),
            HistoryIds = VerificationHistoryTransactions.ToArray(),
            AuthoredFiles = workspace.AssetItems.Where(item => (ReferenceEquals(item.Package, first.Package) || ReferenceEquals(item.Package, second.Package))
                    && File.Exists(item.FullPath.ToOSPath()))
                .Select(item => item.FullPath.ToOSPath())
                .Concat([first.FullPath.ToOSPath(), second.FullPath.ToOSPath(), first.Package.FullPath.ToOSPath(),
                    second.Package.FullPath.ToOSPath(), fixture.Solution]).Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase),
            BaselineLease = projectContent.AcquireLease(), Document = nativeView.Document, Viewport = nativeViewport,
            ViewEntry = nativeView.Entry, ViewState = nativeView.State, SceneContent = nativeView.Content,
            RuntimeEntityIds = nativeViewport.RuntimeScene.RootScene.Entities.Select(entity => entity.Id).Order().ToArray(),
            ActiveEntry = documents.Active,
            Camera = VerificationSceneCamera, HistoryBefore = CaptureActiveProjectParityHistory(sessionHistory),
            HistoryPosition = sessionHistory.History.Position };
        try
        {
            var protectedStates = run.Views.Select(entry => entry.State).OfType<EditorAuthoringState>()
                .Concat(run.History.Stack.RetrieveAllTransactions().SelectMany(run.History.GetParticipants)).ToHashSet();
            var settingsOwnerField = typeof(EngineViewport).GetField("sceneSettingsOwner",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The native settings owner inspection contract changed.");
            var nativeSettingsOwner = settingsOwnerField.GetValue(nativeViewport) as EditorAuthoringState;
            run.StateBaselines = run.States.Select(state => new ActiveProjectStateBaseline(state, state.Generation, state.CanonicalPath,
                state.Document.GetType().FullName ?? state.Document.GetType().Name, protectedStates.Contains(state),
                ReferenceEquals(state, nativeSettingsOwner), state.Document.IsDirty, state.IsPublished,
                state.ViewReferences, state.HistoryReferences)).ToArray();
            StartActiveProjectSwitch(first);
            if (activeProjectBuild != null || !ReferenceEquals(projectContent, run.BaselineContent))
                throw new InvalidOperationException("Selecting the already-current actual project must create no build/adoption.");
            run.Checks.Add(new { contract = "current-target-no-op", noProducer = true });
            activeProjectParityRun = run;
            return run;
        }
        catch { run.BaselineLease.Dispose(); throw; }
    }

    private void AssertActiveProjectParityOwners(ActiveProjectParityRun run)
    {
        sceneDocumentViews.TryGetValue(run.ViewEntry.Id, out var view);
        var nativeViewport = view?.Viewport;
        var actualCamera = nativeViewport?.Camera.Component.ViewMatrix;
        var runtimeIds = nativeViewport?.RuntimeScene?.RootScene?.Entities.Select(entity => entity.Id).Order().ToArray() ?? [];
        var workspaceSame = ReferenceEquals(workspace, run.Workspace);
        var historySame = ReferenceEquals(sessionHistory, run.History);
        var entriesSame = documents.Entries.SequenceEqual(run.Views);
        var activeEntrySame = ReferenceEquals(documents.Active, run.ActiveEntry);
        var canonicalEntrySame = view != null && ReferenceEquals(view.Entry, run.ViewEntry)
            && ReferenceEquals(view.Document, run.Document) && ReferenceEquals(view.State, run.ViewState)
            && ReferenceEquals(run.ViewEntry.Document, run.Document) && ReferenceEquals(run.ViewEntry.State, run.ViewState)
            && ReferenceEquals(view.Workspace, run.Workspace) && IsCurrentSceneDocumentView(view);
        var viewportSame = ReferenceEquals(nativeViewport, run.Viewport)
            && ReferenceEquals(nativeViewport?.PresentedDocument, run.Document)
            && ReferenceEquals(nativeViewport?.SourceContent, run.SceneContent);
        var cameraSame = actualCamera.HasValue && actualCamera.Value == run.Camera;
        var runtimeSame = nativeViewport?.RuntimeScene?.RootScene != null && runtimeIds.SequenceEqual(run.RuntimeEntityIds);
        var historyIdsSame = VerificationHistoryTransactions.SequenceEqual(run.HistoryIds);
        var historyPositionSame = run.History.History.Position == run.HistoryPosition;
        var stateReceipts = run.StateBaselines.Select(baseline =>
        {
            var state = baseline.State;
            var registered = run.History.Registry.Find(baseline.Path);
            var sameCanonical = !state.IsDisposed && state.IsPublished && ReferenceEquals(registered, state)
                && state.Generation == baseline.Generation && string.Equals(state.CanonicalPath, baseline.Path, StringComparison.OrdinalIgnoreCase)
                && state.Document.IsDirty == baseline.Dirty;
            // SynchronizeSceneSettings acquires the new active project's real
            // owner before retiring its previous view lease. CollectUnused may
            // then dispose that clean, unreferenced native settings borrower or
            // another clean cache generation which never held a view lease.
            // Editor entries and history participants can never take this path.
            var activeTargetChanged = run.Adopted.Count != 0 || ReferenceEquals(run.Workspace.Session.CurrentProject, run.Second);
            var retirementEligible = baseline.ViewReferences == 0 || baseline.NativeSettingsBorrower;
            var validRegistrySuccessor = registered == null || registered.Generation != baseline.Generation
                && ReferenceEquals(registered.Registry, run.History.Registry) && registered.IsPublished && !registered.IsDisposed
                && registered.Document.GetType() == state.Document.GetType()
                && string.Equals(registered.CanonicalPath, baseline.Path, StringComparison.OrdinalIgnoreCase);
            // A genuinely cancelled request can also collect idle caches while
            // preparing its native compiler inputs, without adopting a target.
            // It cannot justify retiring a baseline live settings borrower.
            var certifiedCancelledIdleCache = run.CertifiedCancelledCompilerRetirement != null && baseline.ViewReferences == 0;
            var retiredUnreferencedOwner = (activeTargetChanged || certifiedCancelledIdleCache) && !baseline.ProtectedOwner && retirementEligible
                && !baseline.Dirty && baseline.HistoryReferences == 0 && !state.Document.IsDirty
                && state.ViewReferences == 0 && state.HistoryReferences == 0 && state.IsDisposed && !state.IsPublished
                && !ReferenceEquals(registered, state) && validRegistrySuccessor;
            return new { baseline.Generation, baseline.Path, baseline.Type, baseline.ProtectedOwner, baseline.NativeSettingsBorrower,
                before = new { baseline.Dirty, baseline.Published, baseline.ViewReferences, baseline.HistoryReferences },
                current = new { state.Generation, state.CanonicalPath, dirty = state.Document.IsDirty, state.IsPublished, state.IsDisposed,
                    state.ViewReferences, state.HistoryReferences, registeredGeneration = registered?.Generation,
                    registeredType = registered?.Document.GetType().FullName },
                sameCanonical, activeTargetChanged, certifiedCancelledIdleCache,
                cancelledCompilerRetirement = run.CertifiedCancelledCompilerRetirement, retirementEligible, validRegistrySuccessor,
                policy = baseline.ProtectedOwner ? "preserve-editor-or-history-owner"
                    : baseline.ViewReferences == 0 ? "clean-unreferenced-cache-lifetime"
                    : baseline.NativeSettingsBorrower ? "native-settings-lease-transition" : "preserve-other-live-borrower",
                retiredUnreferencedOwner, accepted = sameCanonical || retiredUnreferencedOwner };
        }).ToArray();
        var statesSame = stateReceipts.All(receipt => receipt.accepted);
        var changedFiles = run.AuthoredFiles.Where(pair => !File.Exists(pair.Key)
            || !File.ReadAllBytes(pair.Key).AsSpan().SequenceEqual(pair.Value)).Select(pair => pair.Key).ToArray();
        var receipt = new { phase = run.Phase, workspaceSame, historySame, entriesSame, activeEntrySame,
            canonicalEntrySame, viewportSame, cameraSame, runtimeSame, historyIdsSame, historyPositionSame, statesSame,
            frameLocalDocumentSame = ReferenceEquals(document, run.Document),
            frameLocalViewportSame = ReferenceEquals(engineViewport, run.Viewport),
            expectedEntry = run.ViewEntry.Id, actualEntry = view?.Entry.Id,
            expectedRuntimeIds = run.RuntimeEntityIds, actualRuntimeIds = runtimeIds,
            expectedCamera = ActiveProjectCameraValues(run.Camera),
            actualCamera = actualCamera.HasValue ? ActiveProjectCameraValues(actualCamera.Value) : null,
            expectedHistoryIds = run.HistoryIds, actualHistoryIds = VerificationHistoryTransactions.ToArray(),
            beforeJournal = run.HistoryBefore, actualJournal = CaptureActiveProjectParityHistory(run.History), stateReceipts, changedFiles };
        run.LastOwnerReceipt = receipt;
        if (!workspaceSame || !historySame || !entriesSame || !activeEntrySame || !canonicalEntrySame || !viewportSame
            || !cameraSame || !runtimeSame || !historyIdsSame || !historyPositionSame || !statesSame || changedFiles.Length != 0)
            throw new InvalidOperationException("Active-project native owner preservation failed: "
                + System.Text.Json.JsonSerializer.Serialize(receipt));
    }

    private static object CaptureActiveProjectParityHistory(EditorSessionHistory history)
    {
        var snapshot = history.History;
        return new { position = snapshot.Position, transactions = history.Stack.RetrieveAllTransactions().Select(transaction => new
        {
            transaction.Id, name = snapshot.Entries.FirstOrDefault(entry => entry.Id == transaction.Id)?.Name,
            applied = snapshot.Entries.FirstOrDefault(entry => entry.Id == transaction.Id)?.IsDone,
            savePoint = snapshot.Entries.FirstOrDefault(entry => entry.Id == transaction.Id)?.IsSavePoint,
            operations = transaction.Operations.Select(operation => new { operation.Id, type = operation.GetType().FullName }).ToArray(),
            participants = history.GetParticipants(transaction).Select(state => new
                { state.Generation, state.CanonicalPath, state.AssetId, state.IsPublished, state.IsDisposed }).ToArray()
        }).ToArray() };
    }
    private static float[] ActiveProjectCameraValues(Stride.Core.Mathematics.Matrix value)
        => [value.M11, value.M12, value.M13, value.M14, value.M21, value.M22, value.M23, value.M24,
            value.M31, value.M32, value.M33, value.M34, value.M41, value.M42, value.M43, value.M44];
    internal object? StepActiveProjectParityVerification(ActiveProjectParityRun run)
    {
        AssertActiveProjectParityOwners(run);
        if (run.Phase is 0 or 2)
        {
            var target = run.Phase == 0 ? run.Second : run.First;
            if (IsAuthoringPipelineUpdating || IsActiveProjectSwitching || IsUserProjectBuilding || assetPreviewBuild != null) return null;
            StartActiveProjectSwitch(target);
            var request = activeProjectBuild ?? throw new InvalidOperationException("The production switch did not submit its actual compiler task.");
            if (activeProjectTarget == null || !ReferenceEquals(activeProjectTarget.Workspace, run.Workspace)
                || !ReferenceEquals(activeProjectTarget.Project, target) || !SamePath(activeProjectTarget.Path, target.FullPath.ToOSPath()))
                throw new InvalidOperationException("The actual compiler request is not certified to its canonical target.");
            run.Requests.Add(request); run.Phase++; return null;
        }
        if (run.Phase is 1 or 3)
        {
            if (IsActiveProjectSwitching) return null;
            var request = run.Requests[^1]; var target = run.Phase == 1 ? run.Second : run.First;
            if (!request.IsCompletedSuccessfully || !ReferenceEquals(workspace!.Session.CurrentProject, target)
                || projectContent == null || SamePath(projectContent.OutputPath, run.BaselineContent.OutputPath)
                || !SamePath(projectContent.OutputPath, request.GetAwaiter().GetResult())
                || !SamePath(workspace.CompiledOutput, projectContent.OutputPath))
                throw new InvalidOperationException("The real terminal compiler output was not adopted by its exact native project.");
            run.Adopted.Add(projectContent);
            if (!retainedActiveProjectContent.Contains(run.BaselineContent))
                throw new InvalidOperationException("The compiled owner still required by the original scene view was not retained.");
            var retained = run.BaselineContent.CreateObjectCacheForRetainedLease(run.BaselineLease);
            try
            {
                var firstScene = run.Workspace.AssetItems.First(item => ReferenceEquals(item.Package, run.First.Package) && item.Asset is SceneAsset);
                if (retained.Load<Scene>(ResolveComponentContentUrl(firstScene.Id)).Entities.Count == 0)
                    throw new InvalidOperationException("The exact retained original native bundle no longer resolves its original scene.");
            }
            finally { retained.UnloadAll(); }
            using var lease = projectContent.AcquireLease();
            var cache = projectContent.CreateObjectCache();
            try
            {
                if (run.Phase == 1)
                {
                    var compiledScene = cache.Load<Scene>(ResolveComponentContentUrl(run.Fixture.SceneId));
                    var marker = compiledScene.Entities.Single(entity => entity.Id == run.Fixture.MarkerEntity);
                    if (marker.Get<ModelComponent>()?.Model is not { Meshes.Count: > 0 })
                        throw new InvalidOperationException("The second adopted bundle must resolve its unique native scene and real compiled procedural model.");
                }
                else
                {
                    var firstScene = run.Workspace.AssetItems.First(item => ReferenceEquals(item.Package, run.First.Package) && item.Asset is SceneAsset);
                    if (cache.Load<Scene>(ResolveComponentContentUrl(firstScene.Id)).Entities.Count == 0)
                        throw new InvalidOperationException("The returned first-project compiler bundle cannot resolve its actual original scene.");
                }
            }
            finally { cache.UnloadAll(); }
            run.Checks.Add(new { contract = run.Phase == 1 ? "native-switch" : "native-return", project = target.FullPath.ToOSPath(),
                output = projectContent.OutputPath, exactRequestOutput = true, retainedSceneOwner = true,
                actualTargetSceneCompiled = true });
            run.Phase++; return null;
        }
        if (!RetireActiveProjectParityVerification(run)) return null;
        AssertActiveProjectParityOwners(run);
        return new { passed = true, checks = run.Checks.ToArray(), ownerReceipt = run.LastOwnerReceipt, nativeOriginalProjectReturned = true,
            returnedCompilerOutput = projectContent!.OutputPath, genuineReturnedOwner = ReferenceEquals(projectContent, run.ReturnedContent),
            retainedOutputCount = retainedActiveProjectContent.Count, diagnosticLeaseReleased = run.LeaseReleased,
            scope = "Two actual native SDK projects, real switch/return compiler tasks and output adoption, current-target no-op, retained canonical scene/history. Production retains prior bundles until normal host retirement; this case does not prove their early physical deletion. Stale-target negatives and project-selection gestures are separate." };
    }

    internal bool RetireActiveProjectParityVerification(ActiveProjectParityRun run)
    {
        if (run.Retired) return true;
        if (!ReferenceEquals(workspace, run.Workspace)) throw new InvalidOperationException("The actual switch workspace changed before retirement.");
        if (!run.CleanupStarted)
        {
            // Failure cleanup may cancel an in-flight test switch once. A real return
            // submitted below must remain alive for the production adoption pump.
            if (activeProjectBuild != null) StopActiveProjectSwitch();
            run.CleanupStarted = true;
        }
        if (run.Requests.Any(task => !task.IsCompleted)) return false;
        if (run.CleanupReturn != null)
        {
            if (IsActiveProjectSwitching) return false;
            if (!run.CleanupReturn.IsCompletedSuccessfully || !ReferenceEquals(workspace.Session.CurrentProject, run.First)
                || projectContent == null || !SamePath(projectContent.OutputPath, run.CleanupReturn.Result)
                || !SamePath(workspace.CompiledOutput, projectContent.OutputPath))
                throw new InvalidOperationException("Failure recovery did not genuinely compile and adopt the original native project.");
        }
        else if (!ReferenceEquals(workspace.Session.CurrentProject, run.First))
        {
            if (IsAuthoringPipelineUpdating || IsActiveProjectSwitching || IsUserProjectBuilding || assetPreviewBuild != null
                || IsProjectLoading || IsAddingSolutionProjects || RecoveryTransitionBusy) return false;
            StartActiveProjectSwitch(run.First);
            run.CleanupReturn = activeProjectBuild ?? throw new InvalidOperationException("Native recovery did not submit its actual return compiler.");
            run.Requests.Add(run.CleanupReturn);
            return false;
        }
        if (projectContent == null || !SamePath(workspace.CompiledOutput, projectContent.OutputPath))
            throw new InvalidOperationException("The returned native active project has no matching real adopted content owner.");
        // Retain the genuine current compiler owner and all production-retained
        // bundles. Original scene views own their own leases; only this test's
        // extra diagnostic lease is released here. Normal host retirement owns
        // final worker/GPU/content disposal and physical output deletion.
        if (!run.LeaseReleased) { run.BaselineLease.Dispose(); run.LeaseReleased = true; }
        if (!ReferenceEquals(workspace.Session.CurrentProject, run.First))
            throw new InvalidOperationException("The original native project was not actually returned.");
        run.ReturnedContent = projectContent;
        run.Retired = true;
        if (ReferenceEquals(activeProjectParityRun, run)) activeProjectParityRun = null;
        return true;
    }
    internal bool TryDrainActiveProjectParityVerification()
    {
        var run = activeProjectParityRun;
        if (run == null) return true;
        if (!RetireActiveProjectParityVerification(run)) return false;
        if (!run.Retired || !run.LeaseReleased)
            throw new InvalidOperationException("The native active-project cleanup did not settle its exact retained owner.");
        if (ReferenceEquals(activeProjectParityRun, run)) activeProjectParityRun = null;
        return true;
    }
}
