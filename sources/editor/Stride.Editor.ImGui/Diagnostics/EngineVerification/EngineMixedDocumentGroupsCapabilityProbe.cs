using System.Security.Cryptography;
using Stride.Assets.Sprite;
using Stride.Core.Assets;
using Stride.Core.Assets.Serializers;
using Stride.Core.Reflection;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>Real mixed document groups; authored mutation uses the native service, UI commands use recorded controls.</summary>
internal sealed class EngineMixedDocumentGroupsCapabilityProbe(EditorGame game, string artifacts) : EngineUiCapabilityProbe(game, artifacts)
{
    private string spritePath = "", browserName = "", sceneHash = "", spriteHash = "";
    private Guid sceneId, spriteId;
    private DocumentGroupParityView? sceneBaseline, spriteBaseline, before, closedGroup;
    private MixedSpriteGroupInspection? spriteOwner;
    private string originalName = "";
    private SpriteSheetAsset? persistedBaseline;
    private const string EditedName = "Mixed document retained rename";
    private int commandIndex, popupStep, settled;
    private readonly List<object> checkpoints = [];
    private static readonly EditorDocumentGroupCommand[] Commands =
        [EditorDocumentGroupCommand.Horizontal, EditorDocumentGroupCommand.Previous,
         EditorDocumentGroupCommand.Vertical, EditorDocumentGroupCommand.Float,
         EditorDocumentGroupCommand.Dock, EditorDocumentGroupCommand.Float];
    protected override string Scenario => "E05.groups-mixed";
    protected override object Evidence => new
    {
        spritePath, sceneId, spriteId, commandIndex, popupStep, checkpoints,
        spriteReadiness = spritePath.Length == 0 ? null : Game.InspectMixedSpriteGroupEvidence(spritePath),
        closeLifecycle = Game.InspectMixedCloseLifecycle(),
        scene = Describe(Game.InspectDocumentGroupParity(sceneId)), sprite = Describe(Game.InspectDocumentGroupParity(spriteId)),
        coverage = "Actual native sprite source/canvas and scene; synthetic tab-popup transfers, toolbar undo, close/cancel, save-close and asset-browser reopen. Sprite rename uses canonical native authoring service, not text-entry E2E. No OS pointer/focus/clipboard actions.",
    };

    protected override void Step()
    {
        var io = Gui.GetIO();
        switch (Phase)
        {
            case 0: if (Click("toolbar/open")) Next(); break;
            case 1: if (Click("project/open")) Next(); break;
            case 2:
                if (Game.Document == null || !EnsureScenePresented() || !Game.DocumentGroupParityCaseQuiescent) break;
                if (spritePath.Length == 0)
                {
                    sceneId = Game.VerificationDocumentId(Game.Document)!.Value;
                    sceneBaseline = Game.InspectDocumentGroupParity(sceneId);
                    if (!ReadyScene(sceneBaseline)) break;
                    sceneHash = Hash(sceneBaseline!.Entry.Path);
                    (spritePath, browserName) = Game.PrepareMixedDocumentGroupFixture(Path.Combine(Artifacts, "fixture"));
                    spriteHash = Hash(spritePath);
                    persistedBaseline = AssetFileSerializer.Load<SpriteSheetAsset>(spritePath).Asset;
                }
                if (Click("asset/filter")) Next(); break;
            case 3: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 4:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var character in browserName) io.AddInputCharacter(character);
                Next(); break;
            case 5: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 6: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 7: if (Click("asset/" + browserName)) Next(); break;
            case 8: if (Click("asset/" + browserName)) Next(); break;
            case 9:
                spriteOwner = Game.InspectMixedSpriteGroup(spritePath);
                if (spriteOwner != null && spriteOwner.Document.SelectedSpriteFrame == null)
                { Click("sprites/frame/0"); break; }
                if (!SpriteReady(spriteOwner) || !Game.DocumentGroupParityCaseQuiescent) break;
                spriteId = spriteOwner!.Entry.Id; spriteBaseline = Game.InspectDocumentGroupParity(spriteId);
                if (spriteBaseline?.NativeActive != true) break;
                originalName = spriteOwner.Name;
                Require(spriteBaseline.GroupId == sceneBaseline!.GroupId, "Browser-opened sprite must start in the actual primary scene cohort.");
                checkpoints.Add(new { checkpoint = "mixed-fixture", spriteId, sceneId, nativeSpriteRenderCount = spriteOwner.RenderCount });
                SeekPhase(100); break;
            case 100:
                if (commandIndex == Commands.Length) { SeekPhase(200); break; }
                if (Popup("document-command/" + spriteId + "/" + Commands[commandIndex]))
                { settled = 0; SeekPhase(101); }
                break;
            case 101: ObserveMove(); break;
            case 200:
                if (!CurrentSpriteReady()) break;
                AssertScene();
                Game.RenameMixedSpriteThroughNativeAuthoring(spritePath, EditedName);
                Require(Game.InspectMixedSpriteGroup(spritePath) is { Name: EditedName, Document.IsDirty: true }, "Actual sprite graph rename must dirty only the sprite owner.");
                SeekPhase(201); break;
            case 201: if (Click("toolbar/undo")) Next(); break;
            case 202:
                if (Game.InspectMixedSpriteGroup(spritePath)?.Name != originalName) break;
                Require(Hash(spritePath) == spriteHash, "Undo must not write the native sprite asset file.");
                AssertOwners(); AssertScene();
                Game.RenameMixedSpriteThroughNativeAuthoring(spritePath, EditedName);
                checkpoints.Add(new { checkpoint = "native-edit-ui-undo", originalName, editedName = EditedName });
                popupStep = 0; SeekPhase(203); break;
            case 203: if (Popup("document-close/" + spriteId + "/close")) Next(); break;
            case 204: if (Click("document-close/cancel")) Next(); break;
            case 205:
                if (!CurrentSpriteReady() || Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)) break;
                AssertOwners(); AssertScene();
                Require(Game.InspectMixedSpriteGroup(spritePath) is { Name: EditedName, Document.IsDirty: true, Document.CanUndo: true },
                    "Dirty Close/Cancel must preserve exact native sprite graph and history.");
                closedGroup = Game.InspectDocumentGroupParity(spriteId);
                Require(closedGroup!.Floating && closedGroup.NativeWindow != 0, "Close fixture must occupy an actual detached native group.");
                checkpoints.Add(new { checkpoint = "dirty-close-cancel", group = Describe(closedGroup) });
                popupStep = 0; SeekPhase(206); break;
            case 206: if (Popup("document-close/" + spriteId + "/close")) Next(); break;
            case 207: if (Click("document-close/save")) Next(); break;
            case 208:
                if (!Game.MixedDocumentGroupRemoved(spriteId, closedGroup!.GroupId) || !Game.DocumentGroupParityCaseQuiescent) break;
                if (EditorGame.MixedGroupNativeWindowExists(closedGroup.NativeWindow)) break;
                Require(spriteOwner!.Entry.ViewLease == null, "Closed sprite entry must release its actual presentation lease.");
                Require(Hash(spritePath) != spriteHash, "Save and close must persist the actual sprite rename.");
                AssertIndependentSavedSprite();
                spriteHash = Hash(spritePath); AssertScene();
                checkpoints.Add(new { checkpoint = "saved-close-native-retired", closedGroup.GroupId, closedGroup.NativeWindow, spriteHash });
                SeekPhase(209); break;
            case 209: if (Click("asset/" + browserName)) Next(); break;
            case 210: if (Click("asset/" + browserName)) Next(); break;
            case 211:
                var reopened = Game.InspectMixedSpriteGroup(spritePath);
                if (reopened != null && reopened.Document.SelectedSpriteFrame == null)
                { Click("sprites/frame/0"); break; }
                if (!SpriteReady(reopened) || !Game.DocumentGroupParityCaseQuiescent) break;
                var reopenedGroup = Game.InspectDocumentGroupParity(reopened!.Entry.Id);
                if (reopenedGroup?.NativeActive != true) break;
                Require(reopened.Entry.Id != spriteId && !ReferenceEquals(reopened.Entry, spriteOwner!.Entry)
                    && reopened.Entry.ViewLease != null && reopenedGroup.GroupId == EditorDocumentGroups.PrimaryId,
                    "Actual browser reopen must acquire a fresh presentation entry and primary membership.");
                Require(reopenedGroup.GroupItems.Count(id => id == reopened.Entry.Id) == 1
                    && reopened.Name == EditedName && !reopened.Document.IsDirty && Hash(spritePath) == spriteHash,
                    "Reopened native sprite must contain its saved value exactly once and remain clean.");
                Require(!ReferenceEquals(reopened.Preview, spriteOwner.Preview), "Reopen must allocate a live native canvas rather than reuse the retired preview.");
                AssertScene();
                checkpoints.Add(new { checkpoint = "browser-reopened-primary", entryId = reopened.Entry.Id, group = Describe(reopenedGroup), reopened.Name, reopened.RenderCount });
                Finish(true, "E05.groups-mixed PASS: native sprite/scene group transitions, native edit and UI undo, dirty-close cancel, saved-close native window retirement and actual browser reopen.");
                break;
        }
    }

    private bool Popup(string command)
    {
        if (popupStep == 0)
        {
            if (!Game.DocumentGroupParityCaseQuiescent) return false;
            before = Game.InspectDocumentGroupParity(spriteId);
            var tab = "document-tab/" + spriteId;
            if (before?.NativeActive != true || !Game.Controls.Centers.TryGetValue(tab, out var point)
                || !Aim(tab, point)) return false;
            Gui.GetIO().AddMouseButtonEvent(1, true); popupStep = 1; return false;
        }
        if (popupStep == 1) { Gui.GetIO().AddMouseButtonEvent(1, false); popupStep = 2; return false; }
        if (!Click(command)) return false;
        popupStep = 0; return true;
    }

    private void ObserveMove()
    {
        if (!CurrentSpriteReady()) return;
        var actual = Game.InspectDocumentGroupParity(spriteId)!;
        if (actual.GroupId == before!.GroupId || !actual.NativeActive || actual.GroupActiveId != spriteId) return;
        if (Game.InspectMixedSpriteGroup(spritePath)!.RenderCount <= spriteOwner!.RenderCount) return;
        AssertOwners(); AssertScene();
        var command = Commands[commandIndex];
        if (command is EditorDocumentGroupCommand.Horizontal or EditorDocumentGroupCommand.Vertical)
        {
            var scene = Game.InspectDocumentGroupParity(sceneId)!;
            if (!scene.Visible || scene.RenderRevision <= sceneBaseline!.RenderRevision) return;
            Require(actual.DockId != 0 && scene.DockId != 0 && actual.DockId != scene.DockId
                && actual.ParentDockId != 0 && actual.ParentDockId == scene.ParentDockId,
                "Mixed split must create two actual main-root document panes.");
            Require(actual.ParentSplitAxis == (int)(command == EditorDocumentGroupCommand.Horizontal ? ImGuiAxis.Y : ImGuiAxis.X),
                "Mixed native split axis must match its actual activated menu item.");
        }
        if (command == EditorDocumentGroupCommand.Previous)
            Require(actual.GroupId == EditorDocumentGroups.PrimaryId && actual.GroupItems[0] == spriteId,
                "Mixed Previous must insert the actual sprite first in primary.");
        if (command == EditorDocumentGroupCommand.Float)
        {
            if (!actual.Floating || actual.ViewportId == Gui.GetMainViewport().ID || actual.NativeWindow == 0) return;
            Require(EditorGame.MixedGroupNativeWindowExists(actual.NativeWindow)
                && Math.Abs(actual.Size.X - before.Size.X) <= 2 && Math.Abs(actual.Size.Y - before.Size.Y) <= 2,
                "Mixed Float must create a real usable native window with inherited source bounds.");
        }
        if (command == EditorDocumentGroupCommand.Dock)
        {
            if (actual.Floating || !actual.MainRoot) return;
            if (EditorGame.MixedGroupNativeWindowExists(before.NativeWindow)) return;
            Require(actual.GroupId == Game.InspectDocumentGroupParity(sceneId)!.GroupId,
                "Mixed Dock must restore the real main document cohort.");
        }
        if (++settled < 3) return;
        checkpoints.Add(new { checkpoint = "mixed-native-command", commandIndex, command = command.ToString(), group = Describe(actual),
            nativeSpriteRenderCount = Game.InspectMixedSpriteGroup(spritePath)!.RenderCount,
            retiredFloatingWindow = command == EditorDocumentGroupCommand.Dock ? before.NativeWindow : 0 });
        commandIndex++; SeekPhase(100);
    }

    private bool CurrentSpriteReady() => Game.DocumentGroupParityCaseQuiescent && SpriteReady(Game.InspectMixedSpriteGroup(spritePath));
    private static bool SpriteReady(MixedSpriteGroupInspection? state) => state is { SourceReady: true, Visible: true, RenderCount: > 0, RegisteredVisible: true };
    private static bool ReadyScene(DocumentGroupParityView? state) => state is { NativeActive: true, Visible: true, RenderRevision: > 0, Runtime: not null, View: not null };
    private void AssertOwners()
    {
        var current = Game.InspectMixedSpriteGroup(spritePath)!; var group = Game.InspectDocumentGroupParity(spriteId)!;
        Require(ReferenceEquals(current.Entry, spriteOwner!.Entry) && ReferenceEquals(current.Document, spriteOwner.Document)
            && ReferenceEquals(current.Preview, spriteOwner.Preview) && ReferenceEquals(group.State, spriteBaseline!.State)
            && group.Generation == spriteBaseline.Generation && group.Entry.ViewLease != null,
            "Mixed grouping and cancelled close must preserve exact sprite canonical owner/state/native canvas lease.");
    }
    private void AssertScene()
    {
        var scene = Game.InspectDocumentGroupParity(sceneId)!;
        Require(ReferenceEquals(scene.Entry, sceneBaseline!.Entry) && ReferenceEquals(scene.Document, sceneBaseline.Document)
            && ReferenceEquals(scene.State, sceneBaseline.State) && scene.Generation == sceneBaseline.Generation
            && ReferenceEquals(scene.View, sceneBaseline.View) && ReferenceEquals(scene.Runtime, sceneBaseline.Runtime)
            && ReferenceEquals(scene.Compositor, sceneBaseline.Compositor) && scene.Camera == sceneBaseline.Camera
            && !scene.Document.IsDirty && Hash(scene.Entry.Path) == sceneHash,
            "Mixed commands/edits/close/reopen must preserve the independent scene owner/world/camera and clean file.");
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private void AssertIndependentSavedSprite()
    {
        var loaded = AssetFileSerializer.Load<SpriteSheetAsset>(spritePath).Asset;
        var baseline = persistedBaseline!;
        Require(loaded.Id == baseline.Id && loaded.Sprites.Count == baseline.Sprites.Count && loaded.Sprites[0].Name == EditedName,
            "Independent native YAML reload must certify exact saved sheet identity, frame count and renamed value.");
        var loadedIds = CollectionItemIdHelper.GetCollectionItemIds(loaded.Sprites);
        var baselineIds = CollectionItemIdHelper.GetCollectionItemIds(baseline.Sprites);
        Require(Enumerable.Range(0, loaded.Sprites.Count).All(index => loadedIds[index] == baselineIds[index]),
            "Independent native YAML reload must preserve every original frame ItemId and order.");
        var name = baseline.Sprites[0].Name;
        try
        {
            baseline.Sprites[0].Name = EditedName;
            Require(SerializedHash(loaded) == SerializedHash(baseline),
                "Independent saved sprite bytes must change only the renamed field, preserving all source/region/sibling/sheet properties.");
        }
        finally { baseline.Sprites[0].Name = name; }
        checkpoints.Add(new { checkpoint = "independent-native-sprite-reload", sheetId = loaded.Id,
            frameIds = Enumerable.Range(0, loaded.Sprites.Count).Select(index => loadedIds[index].ToString()).ToArray(), loaded.Sprites[0].Name,
            nativeSerializedSha256 = SerializedHash(loaded), remainingPropertiesUnchanged = true });
    }
    private static string SerializedHash(SpriteSheetAsset asset)
    { using var stream = new MemoryStream(); AssetFileSerializer.Save(stream, asset, null); return Convert.ToHexString(SHA256.HashData(stream.ToArray())); }
    private static object? Describe(DocumentGroupParityView? state) => state == null ? null : new
    { state.GroupId, state.GroupItems, state.GroupActiveId, state.Floating, state.DockId, state.ParentDockId, state.ViewportId, state.NativeWindow,
      position = new { state.Position.X, state.Position.Y }, size = new { state.Size.X, state.Size.Y }, state.NativeActive, state.Visible, state.RenderRevision };
    protected override void CleanInput()
    { Gui.GetIO().AddMouseButtonEvent(1, false); Gui.GetIO().AddKeyEvent(ImGuiKey.ModCtrl, false); Gui.GetIO().AddKeyEvent(ImGuiKey.A, false); Gui.GetIO().AddKeyEvent(ImGuiKey.Enter, false); }
}
