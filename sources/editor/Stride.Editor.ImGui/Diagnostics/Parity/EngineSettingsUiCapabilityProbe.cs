using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>One actual settings UI sequence, with a real isolated durable preferences backend.</summary>
internal sealed class EngineSettingsUiCapabilityProbe : EngineUiCapabilityProbe
{
    private const string Host = "例え.example", Username = "ユーザー", Password = "private-秘密🔐";
    private SettingsUiParitySession? session;
    private bool cancelPreserved, nativeMask, saved;
    protected override string Scenario => "E07.settings";
    protected override object Evidence => new
    {
        coverage = "Synthetic actual Settings controls and real durable private preferences; no physical input.",
        cancelPreserved, nativePasswordFlag = nativeMask, durableUnicodeSave = saved,
        privatePreferencesPath = session?.Path, passwordValuesOmitted = true
    };
    internal EngineSettingsUiCapabilityProbe(EditorGame game, string artifacts) : base(game, artifacts) { }
    protected override void Step()
    {
        var io = Gui.GetIO(); var state = Game.InspectSettingsUiParityDraft();
        switch (Phase)
        {
            case 0: if (Click("toolbar/open")) Next(); break;
            case 1: if (Click("project/open")) Next(); break;
            case 2:
                if (Game.Document == null || !EnsureScenePresented()) break;
                Game.AssertCompiledSceneVerification(); session = Game.BeginSettingsUiParityVerification(Artifacts); Next(); break;
            case 3: if (Click("settings/category/Remote")) Next(); break;
            case 4:
                Require(state.Category == 11 && Game.SettingsUiParityModalOpen, "Remote category must actually become active.");
                if (Click("settings/category/Interface")) Next(); break;
            case 5:
                Require(state.Category == 0, "Interface switching must change the actual settings page.");
                if (Click("settings/category/Remote")) Next(); break;
            case 6: Require(state.Category == 11, "Remote page must restore."); if (Click("settings/remote/Host")) Next(); break;
            case 7: SelectAll(true); Next(); break;
            case 8: SelectAll(false); Type(Host); Next(); break;
            case 9: if (state.Host != Host) break; if (Click("settings/remote/Password")) Next(); break;
            case 10: SelectAll(true); Next(); break;
            case 11: SelectAll(false); Type(Password); Next(); break;
            case 12:
                if (state.Password != Password) break;
                Require(state.NativePassword, "The actual active native password input must use its Password flag."); nativeMask = true;
                if (Click("settings/cancel")) Next(); break;
            case 13:
                if (Game.SettingsUiParityModalOpen) break;
                cancelPreserved = Game.SettingsUiParityCancelPreserved(session!);
                Require(cancelPreserved, "Cancel must retain both the previous preferences owner and exact durable bytes.");
                Game.ReopenSettingsUiParityVerification(); Next(); break;
            case 14: if (Click("settings/category/Remote")) Next(); break;
            case 15:
                Require(state.Host == session!.Baseline.RemoteHost && state.Password == session.Baseline.RemotePassword,
                    "Reopening must discard the edited Remote draft.");
                if (Click("settings/remote/Host")) Next(); break;
            case 16: SelectAll(true); Next(); break;
            case 17: SelectAll(false); Type(Host); Next(); break;
            case 18: if (state.Host != Host) break; if (Click("settings/remote/Username")) Next(); break;
            case 19: SelectAll(true); Next(); break;
            case 20: SelectAll(false); Type(Username); Next(); break;
            case 21: if (state.Username != Username) break; if (Click("settings/remote/Password")) Next(); break;
            case 22: SelectAll(true); Next(); break;
            case 23: SelectAll(false); Type(Password); Next(); break;
            case 24:
                if (state.Password != Password) break;
                Require(state.NativePassword, "Password masking must remain active for the save draft.");
                if (Click("settings/save")) Next(); break;
            case 25:
                if (Game.SettingsUiParityModalOpen) break;
                saved = Game.SettingsUiParitySaved(session!, Host, Username, Password);
                Require(saved, "The actual Save button must publish Unicode Remote fields for independent durable reopen.");
                Finish(true, "E07.settings PASS: actual category/input/Cancel/Save controls preserve draft isolation and publish durable Unicode settings.");
                break;
        }
    }
    private static void SelectAll(bool down)
    { var io = Gui.GetIO(); io.AddKeyEvent(ImGuiKey.ModCtrl, down); io.AddKeyEvent(ImGuiKey.A, down); }
    private static void Type(string text)
    { foreach (var rune in text.EnumerateRunes()) Gui.GetIO().AddInputCharacter((uint)rune.Value); }
    protected override void CleanInput()
    {
        try { if (session != null) Game.EndSettingsUiParityVerification(session); }
        finally { SelectAll(false); }
    }
}
