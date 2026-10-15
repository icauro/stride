using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Task<PreparedSolutionProjects>? addingSolutionProjects;
    private CancellationTokenSource? addingProjectCancellation;
    private ProjectWorkspace? addingProjectWorkspace;
    private PackageContainer[] expectedSolutionProjects = [];
    private long expectedSolutionRevision;
    private SolutionMembershipDocument? solutionMembership;
    private IDisposable? solutionMembershipView;
    private ProjectCreationPanel? addedProjectCreation;
    private bool IsAddingSolutionProjects => addingSolutionProjects != null || addedProjectCreation?.IsGenerating == true;
    private bool CanAddSolutionProject => workspace != null && !string.IsNullOrEmpty(workspace.Session.SolutionPath?.ToOSPath())
        && !IsAddingSolutionProjects && !IsProjectLoading && !IsAuthoringPipelineUpdating && !IsUserProjectBuilding;
    private SolutionMembershipDocument GetSolutionMembershipOwner()
    {
        if (solutionMembership != null) return solutionMembership;
        var current = workspace ?? throw new InvalidOperationException("Open a solution first.");
        var history = SessionAuthoringHistory ?? throw new InvalidOperationException("The solution has no authored session.");
        var state = history.Registry.GetOrCreate(current.Session.SolutionPath.ToOSPath(), null, () => new SolutionMembershipDocument(current, history));
        solutionMembership = state.Document; solutionMembershipView = state.AcquireView(); return state.Document;
    }
    private void DrawSolutionProjectCommands()
    {
        if (Gui.MenuItem(EditorLocalization.Widget("New project...", "Menu"), "", false, CanRequestSolutionProject)) Execute(RequestNewSolutionProject);
        if (Gui.MenuItem(EditorLocalization.Widget("Add existing project...", "Menu"), "", false, CanRequestSolutionProject)) Execute(RequestAddExistingSolutionProject);
    }
    private bool CanRequestSolutionProject => CanAddSolutionProject && !RecoveryTransitionBusy
        && !closeRequested && !closePromptOpen && closingDocument == null && !editorExtensionsStopping;
    private bool IsCurrentSolutionProjectRequest(ProjectWorkspace owner, string solution)
        => ReferenceEquals(workspace, owner) && CanRequestSolutionProject
            && owner.Session.SolutionPath is { } path && SamePath(path.ToOSPath(), solution);
    private void RequestNewSolutionProject()
    {
        if (!CanRequestSolutionProject || workspace is not { } expected || expected.Session.SolutionPath is not { } solution) return;
        var solutionPath = solution.ToOSPath();
        addedProjectCreation = CreateProjectCreationPanel(path => Execute(() =>
        {
            if (!IsCurrentSolutionProjectRequest(expected, solutionPath))
                throw new InvalidOperationException("The solution changed while creating the project.");
            StartAddSolutionProject(path);
        }), existingSolution: true);
        addedProjectCreation.Open(System.IO.Path.GetDirectoryName(solutionPath));
    }
    private void RequestAddExistingSolutionProject()
    {
        if (!CanRequestSolutionProject || workspace is not { } expected || expected.Session.SolutionPath is not { } solution) return;
        var solutionPath = solution.ToOSPath();
        var path = WindowsFileDialogs.Open(Window.NativeWindow.Handle, "Add existing project", "Visual Studio C# project\0*.csproj\0\0",
            System.IO.Path.GetDirectoryName(solutionPath));
        if (path != null && IsCurrentSolutionProjectRequest(expected, solutionPath)) StartAddSolutionProject(path);
    }
    private void StartAddSolutionProject(string path)
    {
        if (!CanAddSolutionProject) throw new InvalidOperationException("A project operation is already running or no solution is open.");
        var current = workspace!;
        ValidatePendingPropertyStrings();
        FinishEntityRename(true);
        if (!ReferenceEquals(workspace, current) || !CanAddSolutionProject)
            throw new InvalidOperationException("The solution changed while finishing pending edits.");
        var owner = GetSolutionMembershipOwner();
        documents.CommitAll();
        if (!ReferenceEquals(workspace, current) || !CanAddSolutionProject)
            throw new InvalidOperationException("The solution changed while committing pending edits.");
        addingProjectWorkspace = current; expectedSolutionRevision = owner.Revision; expectedSolutionProjects = current.Session.Projects.ToArray();
        addingProjectCancellation = new(); var token = addingProjectCancellation.Token;
        addingSolutionProjects = Task.Run(() => PreparedSolutionProjects.Prepare(path, token), token);
        statusMessage = "Importing project...";
    }
    private void PumpAddedSolutionProjects()
    {
        if (addingSolutionProjects?.IsCompleted != true) return;
        var task = addingSolutionProjects; addingSolutionProjects = null;
        var cancelled = addingProjectCancellation?.IsCancellationRequested == true;
        addingProjectCancellation?.Dispose(); addingProjectCancellation = null;
        var expected = addingProjectWorkspace; addingProjectWorkspace = null;
        PreparedSolutionProjects? prepared = null;
        try
        {
            prepared = task.GetAwaiter().GetResult();
            if (cancelled || !ReferenceEquals(workspace, expected)) return;
            if (solutionMembership?.Revision != expectedSolutionRevision || !workspace!.Session.Projects.SequenceEqual(expectedSolutionProjects))
                throw new InvalidOperationException("The solution changed while preparing the project.");
            solutionMembership.Adopt(prepared);
            assetBrowser.RefreshEntries(workspace); assetReferences.Clear();
            statusMessage = "Project imported";
        }
        catch (OperationCanceledException) { statusMessage = "Project import cancelled"; }
        catch (Exception failure)
        { statusMessage = failure.Message; }
        finally
        {
            if (prepared is { IsRetained: false })
                EditorBackgroundJobRetirement.Retire(Task.FromResult(prepared), null, owner => owner.Dispose(), "Unadopted solution preparation");
            expectedSolutionProjects = [];
        }
    }
    private void DrawAddedProjectCreationDialog()
    {
        addedProjectCreation?.Draw();
        if (addingSolutionProjects == null) return;
        if (Gui.Begin("Importing project...", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoDocking))
        {
            Gui.ProgressBar(-1, new System.Numerics.Vector2(320, 0));
            if (Gui.Button(EditorLocalization.Widget("Cancel", "Button"))) addingProjectCancellation?.Cancel();
        }
        ImGuiScopes.EndWindow();
    }
    private void StopAddedSolutionProjects()
    {
        addedProjectCreation?.Cancel(); addingProjectCancellation?.Cancel();
    }
    private void ReleaseSolutionProjectOwnership()
    {
        ReleasePackageSessionMembershipOwnership();
        solutionMembership?.ReleaseRetainedStates(); solutionMembershipView?.Dispose();
        addedProjectCreation?.CancelAndRetireGeneration();
        solutionMembershipView = null; solutionMembership = null; addedProjectCreation = null;
    }
    private void SaveOwnedSolution(Stride.Core.Solutions.Solution solution) => GetSolutionMembershipOwner().Save(solution);
}
