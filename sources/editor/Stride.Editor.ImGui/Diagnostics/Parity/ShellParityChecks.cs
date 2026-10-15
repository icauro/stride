using Hexa.NET.ImGui;
using Stride.Input;
using System.Globalization;
using Stride.Core.Assets.Editor;
using Stride.Core.Translation;

namespace Stride.Editor.ImGui;

internal static class ShellParityChecks
{
    internal static IReadOnlyList<string> Run(string artifactDirectory)
    {
        var checks = new List<string>();
        var directory = Path.Combine(Path.GetFullPath(artifactDirectory), "shell-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "preferences.json");
        var store = new EditorPreferencesStore(path);
        Require(store.TryUpdate(value =>
        {
            value.RemoteHost = "host.example"; value.RemotePort = 2222;
            value.RemoteUsername = "ユーザー"; value.RemotePassword = "private-🔐";
            value.RemoteLocation = "Projects/Сцены"; value.RemoteDisplay = ":1.0";
            value.AskForRemoteCredentials = false;
        }), "Remote settings durable publication");
        var bytes = File.ReadAllBytes(path);
        var snapshot = store.Current;
        Require(!store.TryUpdate(value => value.RemotePort = 0)
            && ReferenceEquals(store.Current, snapshot) && File.ReadAllBytes(path).SequenceEqual(bytes),
            "Invalid remote settings must preserve the previous published snapshot and durable bytes");
        var reopened = new EditorPreferencesStore(path); reopened.Load();
        Require(!reopened.IsBlocked && reopened.Current.RemotePort == 2222
            && reopened.Current.RemoteUsername == "ユーザー" && reopened.Current.RemotePassword == "private-🔐"
            && reopened.Current.RemoteLocation == "Projects/Сцены" && reopened.Current.RemoteDisplay == ":1.0"
            && !reopened.Current.AskForRemoteCredentials, "Remote settings Unicode round trip");
        checks.Add("Remote settings persist Unicode values and reject invalid publication without replacing previous bytes/state");
        ShellPreferencesParityVerification.Verify(directory);
        checks.Add("Actual locked-file preferences preserve state/bytes and retry; blocked malformed/future files recover with exact durable backups");
        Require(ImGuiInput.TryMapKey(Keys.Enter, out var enter) && enter == ImGuiKey.Enter
            && ImGuiInput.TryMapKey(Keys.NumPadEnter, out var keypad) && keypad == ImGuiKey.KeypadEnter
            && ImGuiInput.TryMapKey(Keys.OemBackslash, out var international) && international == ImGuiKey.Oem102,
            "Main/keypad Enter and international OEM key must retain distinct native identities");
        checks.Add("Native keyboard mapping preserves main/keypad Enter and international OEM identity");
        var previousLanguage = TranslationManager.Instance.CurrentLanguage;
        var previousCulture = CultureInfo.CurrentUICulture;
        var previousDefault = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            var labels = new[] { "Copy", "Copy##source-copy", "Copy###source-copy", "##private-identity" };
            EditorLocalization.Initialize(SupportedLanguage.English);
            var englishWidgets = labels.ToDictionary(label => label, label => EditorLocalization.Widget(label, "Menu"));
            EditorLocalization.Initialize(SupportedLanguage.French);
            // The original GameStudio French catalog translates Menu/Copy to Copier.
            Require(EditorLocalization.Text("Copy", "Menu") == "Copier", "The deployed original non-English catalog must be loaded.");
            foreach (var label in labels)
                foreach (var seed in new[] { 0u, 0x12345678u })
                    // ImGuiWindow.GetID uses this actual native hash with its
                    // owner-stack seed. It needs no active presentation window.
                    Require(ImGuiP.ImHashStr(englishWidgets[label], (nuint)0, seed)
                        == ImGuiP.ImHashStr(EditorLocalization.Widget(label, "Menu"), (nuint)0, seed),
                        "Translating visible text must preserve the original native widget identity.");
            Require(EditorLocalization.Widget("##private-identity", "Menu") == "##private-identity",
                "Hidden widget identities must not be translated.");
            Require(ReferenceEquals(EditorLocalization.Widget("Copy", "Menu"), EditorLocalization.Widget("Copy", "Menu")),
                "Repeated literal widgets must reuse their bounded normalized caption cache.");
            checks.Add("Original French catalog lookup and native widget hashes preserve ordinary, hidden and explicit IDs");
        }
        finally
        {
            TranslationManager.Instance.CurrentLanguage = previousLanguage;
            CultureInfo.CurrentUICulture = previousCulture; CultureInfo.DefaultThreadCurrentUICulture = previousDefault;
        }
        VerifyLayoutMigration();
        checks.Add("Legacy window INI migration preserves dock nodes, sizes and authored view IDs while remapping native selected-window references");
        return checks;
    }

    private static void VerifyLayoutMigration()
    {
        var previous = ImGuiP.ImHashStr("Output", (nuint)0, 0).ToString("X8");
        var current = ImGuiP.ImHashStr(EditorPanels.Output.DisplayWindowName, (nuint)0, 0).ToString("X8");
        var scene = ImGuiP.ImHashStr("Scene", (nuint)0, 0).ToString("X8");
        var original = "[Window][Output]\r\nPos=12,34\r\nSize=640,240\r\nViewportId=0x" + previous
            + "\r\nDockId=0xAABBCCDD,1\r\n[Window][Scene]\r\nPos=4,5\r\nSize=600,400\r\n"
            + "[Window][Property grid###Inspector]\r\nDockId=0xDEADBEEF,0\r\n[Docking][Data]\r\n"
            + "DockNode ID=0x" + previous + " Pos=0,0 Size=1280,800 Selected=0x" + previous + "\r\n"
            + "DockSpace ID=0xAABBCCDD Window=0x" + previous + "\r\nDockNode ID=0xDEADBEEF Selected=0x" + scene
            + "\r\n[Other][Data]\r\nSelected=0x" + previous + "\r\n";
        var expected = original.Replace("[Window][Output]", "[Window][" + EditorPanels.Output.DisplayWindowName + "]", StringComparison.Ordinal)
            .Replace("ViewportId=0x" + previous, "ViewportId=0x" + current, StringComparison.Ordinal)
            .Replace("Size=1280,800 Selected=0x" + previous, "Size=1280,800 Selected=0x" + current, StringComparison.Ordinal)
            .Replace("Window=0x" + previous, "Window=0x" + current, StringComparison.Ordinal);
        var migrated = EditorWindowIdentityMigration.Migrate(original);
        Require(migrated == expected && EditorWindowIdentityMigration.Migrate(migrated) == migrated,
            "Migration must change only known window identities/references and must be idempotent.");
        var alreadyCanonical = original + "[Window][" + EditorPanels.Output.DisplayWindowName + "]\r\nSize=800,300\r\n";
        Require(EditorWindowIdentityMigration.Migrate(alreadyCanonical) == alreadyCanonical,
            "An existing canonical window must retain its independently saved layout.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
