using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed record SettingsUiParitySession(EditorPreferencesStore Store, EditorPreferencesStore? Previous,
    EditorPreferences Baseline, string Path, byte[] Bytes);

internal sealed partial class EditorGame
{
    private bool settingsParityNativePassword;
    internal SettingsUiParitySession BeginSettingsUiParityVerification(string artifactDirectory)
    {
        if (!args.Contains("--ui-test") || workspace == null || IsAuthoringPipelineUpdating)
            throw new InvalidOperationException("Settings UI verification requires an idle disposable loaded project.");
        var directory = Path.Combine(Path.GetFullPath(artifactDirectory), "settings-ui-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "preferences.json");
        var store = new EditorPreferencesStore(path);
        if (!store.TryUpdate(value =>
        { value.RemoteHost = "baseline.example"; value.RemoteUsername = "baseline-user"; value.RemotePassword = "baseline-fixture"; }))
            throw new InvalidOperationException("Could not prepare private real settings store: " + store.Error);
        var run = new SettingsUiParitySession(store, preferences, store.Current, path, File.ReadAllBytes(path));
        preferences = store; settingsParityNativePassword = false;
        OpenEditorSettings();
        return run;
    }
    internal void ReopenSettingsUiParityVerification() { settingsParityNativePassword = false; OpenEditorSettings(); }
    internal (int Category, string Host, string Username, string Password, bool NativePassword) InspectSettingsUiParityDraft()
        => (settingsCategory, settingsRemoteHost, settingsRemoteUsername, settingsRemotePassword, settingsParityNativePassword);
    internal unsafe bool SettingsUiParityModalOpen
    {
        get
        {
            var window = ImGuiP.FindWindowByName(EditorLocalization.Widget("Settings", "Title"));
            if (window.IsNull) return false;
            var stack = Gui.GetCurrentContext().OpenPopupStack;
            for (var index = 0; index < stack.Size; index++)
                if (stack[index].Window != null && stack[index].Window->ID == window.ID) return true;
            return false;
        }
    }
    internal bool SettingsUiParityCancelPreserved(SettingsUiParitySession run)
        => ReferenceEquals(preferences, run.Store) && ReferenceEquals(run.Store.Current, run.Baseline)
            && File.ReadAllBytes(run.Path).SequenceEqual(run.Bytes);
    internal bool SettingsUiParitySaved(SettingsUiParitySession run, string host, string username, string password)
    {
        if (!ReferenceEquals(preferences, run.Store) || ReferenceEquals(run.Store.Current, run.Baseline)) return false;
        var reopened = new EditorPreferencesStore(run.Path); reopened.Load();
        return !reopened.IsBlocked && reopened.Current.RemoteHost == host
            && reopened.Current.RemoteUsername == username && reopened.Current.RemotePassword == password
            && run.Store.Current.RemoteHost == host && run.Store.Current.RemoteUsername == username
            && run.Store.Current.RemotePassword == password;
    }
    internal unsafe void EndSettingsUiParityVerification(SettingsUiParitySession run)
    {
        if (!ReferenceEquals(preferences, run.Store)) throw new InvalidOperationException("The settings UI fixture owner changed.");
        try
        {
            var window = ImGuiP.FindWindowByName(EditorLocalization.Widget("Settings", "Title"));
            var stack = Gui.GetCurrentContext().OpenPopupStack;
            for (var index = 0; !window.IsNull && index < stack.Size; index++)
                if (stack[index].Window != null && stack[index].Window->ID == window.ID)
                { ImGuiP.ClosePopupToLevel(index, true); break; }
        }
        finally { preferences = run.Previous; LoadSettingsDraft(); }
    }
    private void RecordSettingsParityControl(string id)
    { if (Controls.Enabled) Controls.Record("settings/" + id); }
    private void RecordSettingsParityInput(string label)
    {
        RecordSettingsParityControl("remote/" + label);
        if (label != "Password") return;
        var input = ImGuiP.GetInputTextState(Gui.GetItemID());
        if (!input.IsNull && Gui.IsItemActive()) settingsParityNativePassword = (input.Flags & ImGuiInputTextFlags.Password) != 0;
    }
}
