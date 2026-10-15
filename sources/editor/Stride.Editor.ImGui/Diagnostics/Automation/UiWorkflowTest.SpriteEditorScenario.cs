using Hexa.NET.ImGui;
using Stride.Assets.Sprite;
using Stride.Core.Assets;
using Stride.Core.Reflection;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class UiWorkflowTest
{
    private string? spriteOriginalName, spriteItemIdentity;
    private int spriteOriginalCount;
    private const string SpriteEditedName = "U06 sprite – 星";

    private void UpdateSpriteEditorScenario()
    {
        var io = Gui.GetIO();
        switch (stage)
        {
            case 11700:
                Assert(spriteAuthoredFixture != null, "Sprite authored fixture must be prepared before UI loading");
                if (!game.VerificationFamilyBrowserRecursive) { stage = 11720; break; }
                if (Click("asset/filter")) Next(); break;
            case 11701: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 11702:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var c in spriteAuthoredFixture!.AssetName) io.AddInputCharacter(c);
                Next(); break;
            case 11703: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 11704: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 11705:
                if (game.VerificationFamilyAssetControl(spriteAuthoredFixture!.AssetId) is { } control && Click(control)) Next(); break;
            case 11706: if (Click("asset/edit")) Next(); break;
            case 11707:
                var opened = game.InspectSpriteAuthoredName();
                if (opened.AssetId != spriteAuthoredFixture!.AssetId.ToString()) break;
                Assert(opened.Count > 0 && opened.ItemIdentity != null && !opened.Dirty, "Sprite editor must contain actual identified source frames");
                spriteOriginalName = opened.Name; spriteItemIdentity = opened.ItemIdentity; spriteOriginalCount = opened.Count;
                if (!game.Controls.Centers.ContainsKey("sprites/frame/0"))
                {
                    if (game.Controls.Centers.TryGetValue("sprites/list/body", out var listBody))
                    {
                        io.AddMousePosEvent(listBody.X, listBody.Y); io.AddMouseWheelEvent(0, -1);
                        RecordAction("scroll-sprite-list", "sprites/frame/0"); wait = 2; break;
                    }
                    // Authored activation and native dock visibility are separate:
                    // activate the actual document tabs before selecting a frame.
                    if (game.InspectActiveAuthoredDocumentTabControl() is { } tab && game.Controls.Centers.ContainsKey(tab)) Click(tab);
                    else TryClickToolTab("editor.document");
                    break;
                }
                if (Click("sprites/frame/0")) Next(); break;
            case 11708:
                if (ClickInspectorControl("sprites/name")) Next(); break;
            case 11709: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 11710:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var c in SpriteEditedName) io.AddInputCharacter(c);
                Next(); break;
            case 11711: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 11712: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 11713: if (Click("toolbar/undo")) Next(); break;
            case 11714:
                var undone = game.InspectSpriteAuthoredName();
                Assert(undone.Name == spriteOriginalName && !undone.Dirty && undone.CanRedo, "Sprite undo must restore original frame name/save point");
                if (Click("toolbar/redo")) Next(); break;
            case 11715:
                var redone = game.InspectSpriteAuthoredName();
                Assert(redone.Name == SpriteEditedName && redone.Dirty && redone.ItemIdentity == spriteItemIdentity
                    && redone.Count == spriteOriginalCount && redone.CanUndo, "Sprite redo must restore actual edit without changing frame identities/count");
                if (Click("toolbar/save")) Next(); break;
            case 11716:
                var saved = game.InspectSpriteAuthoredName();
                if (saved.Dirty) break;
                var disk = AssetFileSerializer.Load<SpriteSheetAsset>(spriteAuthoredFixture!.AssetPath).Asset;
                Assert(disk.Id == spriteAuthoredFixture.AssetId && disk.Sprites.Count == spriteOriginalCount && disk.Sprites[0].Name == SpriteEditedName
                    && disk.Sprites.Skip(1).Select(frame => frame.Name).SequenceEqual(spriteSiblingNames)
                    && CollectionItemIdHelper.TryGetCollectionItemIds(disk.Sprites, out var ids) && ids[0].ToString() == spriteItemIdentity,
                    "Independent native sprite reload must preserve edited name, actual asset/item identity and siblings");
                checks.Add("Sprite frame name widget/undo/redo/save preserves native frame identities and independently reloads");
                CompleteIndependentSubset(); break;
            case 11720:
                if (game.VerificationFamilyBrowserRecursive) { stage = 11700; break; }
                // Observe actual popup lifecycle: input dispatch alone is not
                // menu activation. Missing submenu rows must not trigger the
                // generic asset-tab fallback, which closes the pending popup.
                var optionsControl = game.Controls.Centers.ContainsKey("asset/options/recursive") ? "asset/options/recursive"
                    : game.Controls.Centers.ContainsKey("asset/options/display") ? "asset/options/display" : "asset/options";
                Click(optionsControl);
                break;
        }
    }
}
