using System.Security.Cryptography;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>Actual native tab-popup commands with synthetic input over two compiled scene owners.</summary>
internal sealed class EngineDocumentGroupsCapabilityProbe : EngineUiCapabilityProbe
{
    private MultiViewFixtureVerification? fixture;
    private SceneDocument? first;
    private EntityHierarchyDocument? second;
    private DocumentGroupParityView? firstBaseline, secondBaseline;
    private Guid firstId, secondId, firstEntity, secondEntity;
    private string? firstDisk, secondDisk;
    private int commandIndex, menuStep;
    private DocumentGroupParityView? sourceBefore, otherBefore;
    private int settledFrames;
    private readonly Action<string>? saveLayout;
    private readonly Func<string, Task<bool>>? restoreLayout;
    private Task<bool>? layoutRestore;
    private bool commandSequenceVerified;
    private DocumentGroupParityView? savedFirst, savedSecond;
    private string? savedLayoutHash;
    private string LayoutPath => Path.Combine(Artifacts, "document-groups-layout.ini");
    private (bool Second, EditorDocumentGroupCommand Command) CurrentCommand
        => commandIndex < Commands.Length ? Commands[commandIndex] : (true, EditorDocumentGroupCommand.Float);
    private readonly List<object> checkpoints = [];
    private static readonly (bool Second, EditorDocumentGroupCommand Command)[] Commands =
    [
        (true, EditorDocumentGroupCommand.Horizontal), (false, EditorDocumentGroupCommand.Next),
        (false, EditorDocumentGroupCommand.Previous), (true, EditorDocumentGroupCommand.Float),
        (true, EditorDocumentGroupCommand.Dock), (true, EditorDocumentGroupCommand.Vertical),
        (true, EditorDocumentGroupCommand.Previous),
    ];
    protected override string Scenario => saveLayout == null ? "E05.groups" : "E05.groups-layout";
    protected override object Evidence => new
    {
        firstId, secondId, firstEntity, secondEntity, commandIndex, menuStep, checkpoints, savedLayoutHash,
        savedFirst = Describe(savedFirst), savedSecond = Describe(savedSecond),
        first = Describe(Game.InspectDocumentGroupParity(firstId)), second = Describe(Game.InspectDocumentGroupParity(secondId)),
        coverage = "Actual tab context-menu split/transfer/float/dock activation with synthetic ImGui pointer input; no physical cursor, native window drag or foreground manipulation.",
        floatingBoundary = "Float requires a distinct live native viewport and HWND under the dedicated background document-group backend.",
        platformViewportBackendEnabled = (Gui.GetIO().BackendFlags & ImGuiBackendFlags.PlatformHasViewports) != 0,
        detachedOsWindowCoverage = "Synthetic context-menu commands over actual native windows. Physical window dragging, foreground activation and desktop cursor are not exercised.",
    };
    internal EngineDocumentGroupsCapabilityProbe(EditorGame game, string artifacts,
        Action<string>? saveLayout = null, Func<string, Task<bool>>? restoreLayout = null) : base(game, artifacts)
    {
        if ((saveLayout == null) != (restoreLayout == null)) throw new ArgumentException("Layout save and restore require both production delegates.");
        this.saveLayout = saveLayout; this.restoreLayout = restoreLayout;
    }

    protected override void Step()
    {
        var io = Gui.GetIO();
        switch (Phase)
        {
            case 0:
                fixture ??= Game.PrepareMultiViewVerificationFixture();
                if (Click("toolbar/open")) Next(); break;
            case 1: if (Click("project/open")) Next(); break;
            case 2:
                if (Game.Document == null || !EnsureScenePresented()) break;
                if (Game.Document.Selected?.Name != "Sphere") { Click("entity/Sphere"); break; }
                first = Game.Document; firstId = Game.VerificationDocumentId(first)!.Value; firstEntity = first.Selected.Id;
                firstBaseline = Game.InspectDocumentGroupParity(firstId);
                if (!Ready(firstBaseline, visible: true)) break;
                firstDisk = FileHash(first.Path);
                if (Click("asset/filter")) Next(); break;
            case 3: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 4:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var character in "CapabilitySecondScene") io.AddInputCharacter(character);
                Next(); break;
            case 5: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 6: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 7: if (Click("asset/CapabilitySecondScene")) Next(); break;
            case 8: if (Click("asset/CapabilitySecondScene")) Next(); break;
            case 9:
                second = Game.VerificationActiveHierarchy;
                if (second == null || second.Asset.Id != fixture!.SceneId) break;
                if (second.Selected?.Name != "Secondary Imported cube") { Click("entity/Secondary Imported cube"); break; }
                secondId = Game.VerificationDocumentId(second)!.Value; secondEntity = second.Selected.Id;
                secondBaseline = Game.InspectDocumentGroupParity(secondId);
                if (!Ready(secondBaseline, visible: true) || !Game.DocumentGroupParityCaseQuiescent) break;
                Require(!ReferenceEquals(firstBaseline!.View, secondBaseline!.View)
                    && !ReferenceEquals(firstBaseline.Runtime, secondBaseline.Runtime)
                    && !ReferenceEquals(firstBaseline.Compositor, secondBaseline.Compositor),
                    "The fixture must have independently owned native worlds, compositors and view objects.");
                Require(firstBaseline.GroupId == secondBaseline.GroupId, "The initial fixture must share one actual document cohort.");
                secondDisk = FileHash(second.Path);
                checkpoints.Add(new { checkpoint = "initial-cohort", first = Describe(firstBaseline), second = Describe(secondBaseline) });
                SeekPhase(100); break;
            case 100: DispatchCommand(); break;
            case 101: ObserveCommand(); break;
            case 102:
                var a = Game.InspectDocumentGroupParity(firstId); var b = Game.InspectDocumentGroupParity(secondId);
                if (!Ready(a, false) || !Ready(b, true) || !Game.DocumentGroupParityCaseQuiescent) break;
                AssertOwners(a!, b!);
                Require(a!.GroupId == b!.GroupId && !a.Floating && !b.Floating && a.MainRoot && b.MainRoot,
                    "Final transfer must reunite both real document entries in the main dockspace.");
                Require(b.GroupItems.SequenceEqual(new[] { secondId, firstId }), "Previous transfer inserts the moved tab first in its actual destination cohort.");
                Require(FileHash(first!.Path) == firstDisk && FileHash(second!.Path) == secondDisk && !first.IsDirty && !second.IsDirty,
                    "Presentation transitions must not rewrite or dirty either native scene asset.");
                checkpoints.Add(new { checkpoint = "final-main-cohort", first = Describe(a), second = Describe(b) });
                commandSequenceVerified = true;
                if (saveLayout != null) { SeekPhase(100); break; }
                Finish(true, "E05.groups PASS: actual horizontal/vertical tab-popup splits, next/previous transfers including empty primary, float/dock and retained independent native world/view/camera owners.");
                break;
            case 200:
                savedFirst = Game.InspectDocumentGroupParity(firstId); savedSecond = Game.InspectDocumentGroupParity(secondId);
                if (!Ready(savedFirst, true) || !Ready(savedSecond, true) || !Game.DocumentGroupParityCaseQuiescent) break;
                AssertOwners(savedFirst!, savedSecond!);
                Require(savedFirst!.GroupId != savedSecond!.GroupId && savedSecond.Floating,
                    "Persisted layout must contain independently visible main and native floating document groups.");
                saveLayout!(LayoutPath);
                Require(File.Exists(LayoutPath), "Production layout save must create the actual private layout file.");
                savedLayoutHash = FileHash(LayoutPath);
                checkpoints.Add(new { checkpoint = "layout-saved", first = Describe(savedFirst), second = Describe(savedSecond), savedLayoutHash });
                SeekPhase(201); break;
            case 201: if (Click("menu/view")) Next(); break;
            case 202: if (Click("menu/view/reset-layout")) Next(); break;
            case 203:
                var resetFirst = Game.InspectDocumentGroupParity(firstId); var resetSecond = Game.InspectDocumentGroupParity(secondId);
                if (Game.WorkflowLayoutResetPending || !Ready(resetFirst, false) || !Ready(resetSecond, false)
                    || !Game.DocumentGroupParityCaseQuiescent || resetFirst!.GroupId != resetSecond!.GroupId) break;
                AssertOwners(resetFirst!, resetSecond!);
                Require(resetFirst.GroupId == EditorDocumentGroups.PrimaryId, "Actual Reset layout must reunite both retained document owners in primary.");
                Require(FileHash(LayoutPath) == savedLayoutHash, "UI layout reset must not overwrite the explicit saved layout checkpoint.");
                checkpoints.Add(new { checkpoint = "layout-reset", first = Describe(resetFirst), second = Describe(resetSecond) });
                if (Click("document-tab/" + firstId)) SeekPhase(205);
                break;
            case 205:
                if (Game.VerificationActiveHierarchy != first || !Ready(Game.InspectDocumentGroupParity(firstId), true)
                    || !Game.DocumentGroupParityCaseQuiescent) break;
                checkpoints.Add(new { checkpoint = "layout-active-owner-changed", activeDocument = firstId });
                layoutRestore = restoreLayout!(LayoutPath);
                settledFrames = 0; SeekPhase(204); break;
            case 204:
                if (layoutRestore?.IsCompleted != true) break;
                Require(layoutRestore.GetAwaiter().GetResult(), "Production layout loading must accept its own saved file before the next native frame.");
                var restoredFirst = Game.InspectDocumentGroupParity(firstId); var restoredSecond = Game.InspectDocumentGroupParity(secondId);
                if (!Ready(restoredFirst, true) || !Ready(restoredSecond, true) || !Game.DocumentGroupParityCaseQuiescent) break;
                if (restoredFirst!.GroupId != savedFirst!.GroupId || restoredSecond!.GroupId != savedSecond!.GroupId) break;
                AssertOwners(restoredFirst, restoredSecond);
                AssertRestoredGroup(restoredFirst, savedFirst); AssertRestoredGroup(restoredSecond, savedSecond);
                Require(Game.VerificationActiveHierarchy == second && restoredFirst.RenderRevision > savedFirst.RenderRevision
                    && restoredSecond.RenderRevision > savedSecond.RenderRevision,
                    "Layout restore must retain the active document and advance both independent native worlds.");
                Require(restoredSecond.ViewportId != Gui.GetMainViewport().ID && restoredSecond.NativeWindow != 0
                    && restoredSecond.NativeWindow != restoredFirst.NativeWindow,
                    "Restored floating group must own a distinct actual native window.");
                Require(FileHash(first!.Path) == firstDisk && FileHash(second!.Path) == secondDisk && FileHash(LayoutPath) == savedLayoutHash,
                    "Layout round trip must preserve clean scene files and the persisted checkpoint.");
                if (++settledFrames < 3) break;
                checkpoints.Add(new { checkpoint = "layout-restored", first = Describe(restoredFirst), second = Describe(restoredSecond) });
                Finish(true, "E05.groups-layout PASS: seven actual group commands plus production save, UI reset and persisted native layout restore with retained document/world/camera owners.");
                break;
        }
    }

    private void DispatchCommand()
    {
        if (commandIndex >= Commands.Length && saveLayout == null) { SeekPhase(102); return; }
        if (commandIndex == Commands.Length && !commandSequenceVerified)
        { SeekPhase(102); return; }
        if (commandIndex > Commands.Length) { SeekPhase(200); return; }
        var command = CurrentCommand; var id = command.Second ? secondId : firstId;
        var control = "document-command/" + id + "/" + command.Command;
        if (menuStep == 0)
        {
            if (!Game.DocumentGroupParityCaseQuiescent) return;
            var current = Game.InspectDocumentGroupParity(id);
            if (current == null || !current.NativeActive) return;
            var tab = "document-tab/" + id;
            if (!Game.Controls.Centers.TryGetValue(tab, out var point) || !Aim(tab, point)) return;
            sourceBefore = current;
            otherBefore = Game.InspectDocumentGroupParity(command.Second ? firstId : secondId);
            Gui.GetIO().AddMouseButtonEvent(1, true); menuStep = 1; return;
        }
        if (menuStep == 1) { Gui.GetIO().AddMouseButtonEvent(1, false); menuStep = 2; return; }
        if (Click(control)) { settledFrames = 0; SeekPhase(101); }
    }

    private void ObserveCommand()
    {
        var a = Game.InspectDocumentGroupParity(firstId); var b = Game.InspectDocumentGroupParity(secondId);
        if (!Ready(a, false) || !Ready(b, false) || !Game.DocumentGroupParityCaseQuiescent) return;
        AssertOwners(a!, b!);
        var step = CurrentCommand; var moved = step.Second ? b! : a!; var other = step.Second ? a! : b!;
        if (moved.GroupId == sourceBefore!.GroupId) return;
        if (Game.VerificationActiveHierarchy != moved.Document || !moved.Visible || !moved.NativeActive
            || moved.RenderRevision <= sourceBefore.RenderRevision) return;
        if (Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)) return;
        switch (step.Command)
        {
            case EditorDocumentGroupCommand.Horizontal:
            case EditorDocumentGroupCommand.Vertical:
                if (!other.Visible || other.RenderRevision <= otherBefore!.RenderRevision) return;
                if (moved.DockId == 0 || other.DockId == 0 || moved.DockId == other.DockId
                    || moved.ParentDockId == 0 || moved.ParentDockId != other.ParentDockId) return;
                Require(moved.MainRoot && other.MainRoot && !moved.Floating && !other.Floating,
                    "A main-document split must retain two actual main-root native dock panes.");
                var axis = step.Command == EditorDocumentGroupCommand.Horizontal ? (int)ImGuiAxis.Y : (int)ImGuiAxis.X;
                Require(moved.ParentSplitAxis == axis && other.ParentSplitAxis == axis, "The actual native split axis must match the activated menu command.");
                Require(step.Command == EditorDocumentGroupCommand.Horizontal ? moved.Position.Y > other.Position.Y : moved.Position.X > other.Position.X,
                    "Native document pane geometry must match the split orientation.");
                break;
            case EditorDocumentGroupCommand.Next:
            case EditorDocumentGroupCommand.Previous:
                var destination = commandIndex == 2 ? EditorDocumentGroups.PrimaryId : otherBefore!.GroupId;
                Require(moved.GroupId == destination && moved.GroupItems[0] == moved.Entry.Id,
                    "Native sibling transfer must move the actual entry to its destination cohort's first tab.");
                if (commandIndex == 1) Require(moved.GroupItems.SequenceEqual(new[] { firstId, secondId }), "Next must preserve destination-first insertion and the second native tab.");
                if (commandIndex == 2) Require(moved.GroupId == EditorDocumentGroups.PrimaryId, "Previous must repopulate the retained empty primary group.");
                break;
            case EditorDocumentGroupCommand.Float:
                if (!moved.Floating || moved.MainRoot) return;
                Require((Gui.GetIO().BackendFlags & ImGuiBackendFlags.PlatformHasViewports) != 0,
                    "Native document-group acceptance requires its dedicated isolated platform viewport backend.");
                if (moved.ViewportId == Gui.GetMainViewport().ID || moved.NativeWindow == 0)
                {
                    if (++settledFrames < 6) return;
                    Require(false, "Float remained inside the main viewport instead of creating its own native window.");
                }
                Require(moved.NativeActive && moved.Size.X > 0 && moved.Size.Y > 0
                    && moved.ViewportId != other.ViewportId && moved.NativeWindow != other.NativeWindow,
                    "Float must expose a distinct live native viewport and HWND for the actual document group.");
                Require(Math.Abs(moved.Size.X - sourceBefore.Size.X) <= 2
                    && Math.Abs(moved.Size.Y - sourceBefore.Size.Y) <= 2,
                    "Float must inherit the source document pane's usable native size within two pixels.");
                break;
            case EditorDocumentGroupCommand.Dock:
                if (moved.Floating || !moved.MainRoot || moved.DockId == 0) return;
                Require(moved.GroupId == other.GroupId, "Dock must return the actual floating entry to the main document cohort.");
                break;
        }
        var hierarchy = (EntityHierarchyDocument)moved.Document;
        var entity = hierarchy.Asset.Hierarchy.Parts[step.Second ? secondEntity : firstEntity].Entity;
        Game.AssertRuntimeEntityVerification(entity.Id, entity.Transform.Parent?.Entity.Id);
        if (++settledFrames < 3) return;
        checkpoints.Add(new { commandIndex, command = step.Command.ToString(), moved = Describe(moved), other = Describe(other),
            emptyPrimaryRepopulated = commandIndex == 2, nativeDetachedViewport = moved.ViewportId != Gui.GetMainViewport().ID });
        commandIndex++; menuStep = 0; SeekPhase(100);
    }

    private void AssertOwners(DocumentGroupParityView a, DocumentGroupParityView b)
    {
        Preserved(a, firstBaseline!); Preserved(b, secondBaseline!);
        Require(!first!.IsDirty && !second!.IsDirty, "Document grouping must not dirty authored scene data.");
        Require(first.Asset.Hierarchy.Parts.ContainsKey(firstEntity) && second.Asset.Hierarchy.Parts.ContainsKey(secondEntity)
            && !first.Asset.Hierarchy.Parts.ContainsKey(secondEntity) && !second.Asset.Hierarchy.Parts.ContainsKey(firstEntity),
            "Native group transitions must preserve distinct authored entity-world identities.");
    }
    private static void AssertRestoredGroup(DocumentGroupParityView actual, DocumentGroupParityView saved)
    {
        Require(actual.GroupItems.SequenceEqual(saved.GroupItems) && actual.GroupActiveId == saved.GroupActiveId,
            "Persisted document groups must restore exact tab membership, order and active document.");
        Require(actual.Floating == saved.Floating && actual.MainRoot == saved.MainRoot
            && actual.DockId == saved.DockId && actual.RootDockId == saved.RootDockId,
            "Persisted native document groups must restore their actual dock topology.");
        Require(Math.Abs(actual.Position.X - saved.Position.X) <= 2 && Math.Abs(actual.Position.Y - saved.Position.Y) <= 2
            && Math.Abs(actual.Size.X - saved.Size.X) <= 2 && Math.Abs(actual.Size.Y - saved.Size.Y) <= 2,
            "Persisted native document groups must restore position and usable size within two pixels.");
    }
    private static void Preserved(DocumentGroupParityView current, DocumentGroupParityView original)
        => Require(ReferenceEquals(current.Entry, original.Entry) && ReferenceEquals(current.Document, original.Document)
            && ReferenceEquals(current.State, original.State) && current.Generation == original.Generation
            && ReferenceEquals(current.View, original.View) && ReferenceEquals(current.Runtime, original.Runtime)
            && ReferenceEquals(current.Compositor, original.Compositor) && current.Camera == original.Camera,
            "A native group transition must retain the exact entry, document, authoring generation, view, world, compositor and camera.");
    private static bool Ready(DocumentGroupParityView? state, bool visible) => state is { NativeActive: true, View: not null, Runtime: not null, Compositor: not null }
        && (!visible || state.Visible) && state.RenderRevision > 0;
    private static string FileHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static object? Describe(DocumentGroupParityView? state) => state == null ? null : new
    {
        documentId = state.Entry.Id, assetPath = state.Entry.Path, generation = state.Generation, state.GroupId, state.GroupItems,
        state.Floating, state.DockId, state.RootDockId, state.ParentDockId, state.ParentSplitAxis, state.MainRoot,
        state.ViewportId, state.NativeWindow, position = new { state.Position.X, state.Position.Y }, size = new { state.Size.X, state.Size.Y },
        state.NativeActive, state.Visible, state.RenderRevision, camera = state.Camera.ToArray(),
        state.NativeTabs,
        state.GroupActiveId,
    };
    protected override void CleanInput() => Gui.GetIO().AddMouseButtonEvent(1, false);
}
