using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool openEditorSettings;
    private int settingsCategory;
    private int settingsTheme;
    private CameraKeyBindings settingsCameraBindings = new();
    private bool settingsInvertPanningAxis;
    private int settingsScriptFontSize;
    private bool settingsReloadSession, settingsResetLayout;
    private bool settingsAskBeforeDeletingEntities;
    private bool settingsAskBeforeDeletingUIElements;
    private bool settingsAskBeforeDeletingAssets;
    private bool settingsAskBeforeSavingNewScripts;
    private bool settingsLinearMovementForRotation;
    private string settingsDefaultSessionPath = "";
    private string settingsRemoteHost = "", settingsRemoteUsername = "", settingsRemotePassword = "";
    private string settingsRemoteLocation = "Projects", settingsRemoteDisplay = ":0.0";
    private int settingsRemotePort = 22;
    private bool settingsAskForRemoteCredentials = true;
    private bool settingsAskBeforeReloadingAssemblies, settingsAutoReloadAssemblies;
    private bool settingsDisplayDirectionNames;
    private bool settingsUseEffectCompilerServer;
    private Stride.Core.Assets.Editor.SupportedLanguage settingsLanguage;
    private float settingsTranslationSnap, settingsRotationSnap, settingsScaleSnap;
    private string? editorSettingsError;
    private SceneTransformGizmos? configuredGizmos;
    private static readonly EditorPreferences FallbackSceneEditorPreferences = new();

    private void OpenEditorSettings()
    {
        LoadSettingsDraft(); settingsResetLayout = false; editorSettingsError = null;
        openEditorSettings = true;
    }

    private void LoadSettingsDraft()
    {
        RefreshProjectStartupPreferences(true);
        var current = preferences?.Current ?? new EditorPreferences();
        settingsCameraBindings = current.CameraBindings.Clone(); settingsInvertPanningAxis = current.InvertPanningAxis;
        settingsTheme = (int)current.Theme; settingsReloadSession = current.ReloadLastSession;
        settingsScriptFontSize = current.ScriptEditorFontSize;
        settingsAskBeforeDeletingEntities = current.AskBeforeDeletingEntities;
        settingsAskBeforeDeletingUIElements = current.AskBeforeDeletingUIElements;
        settingsAskBeforeDeletingAssets = current.AskBeforeDeletingAssets;
        settingsAskBeforeSavingNewScripts = current.AskBeforeSavingNewScripts;
        settingsDefaultSessionPath = current.DefaultSessionPath ?? "";
        settingsLinearMovementForRotation = current.UseLinearMovementForRotation;
        settingsAskBeforeReloadingAssemblies = current.AskBeforeReloadingAssemblies; settingsAutoReloadAssemblies = current.AutoReloadAssemblies;
        settingsDisplayDirectionNames = current.DisplayDirectionNames;
        settingsIde = current.DefaultIDE;
        settingsTextEditor = current.DefaultTextEditor; settingsShaderEditor = current.ShaderEditor;
        settingsDebugOutputLevel = current.DebugOutputLevel;
        settingsGraphicsApi = current.GraphicsApi;
        settingsUseEffectCompilerServer = current.UseEffectCompilerServer;
        settingsLanguage = current.Language;
        settingsRemoteHost = current.RemoteHost; settingsRemotePort = current.RemotePort;
        settingsRemoteUsername = current.RemoteUsername; settingsRemotePassword = current.RemotePassword;
        settingsRemoteLocation = current.RemoteLocation; settingsRemoteDisplay = current.RemoteDisplay;
        settingsAskForRemoteCredentials = current.AskForRemoteCredentials;
        settingsTranslationSnap = current.DefaultTranslationSnap;
        settingsRotationSnap = current.DefaultRotationSnap; settingsScaleSnap = current.DefaultScaleSnap;
    }

    private void DrawEditorSettings()
    {
        DrawSettingsRestartNotice();
        if (openEditorSettings) { Gui.OpenPopup(EditorLocalization.Widget("Settings", "Title")); openEditorSettings = false; }
        var workSize = Gui.GetMainViewport().WorkSize;
        var initialSize = UiScale(1024, 768);
        initialSize.X = Math.Min(initialSize.X, workSize.X);
        initialSize.Y = Math.Min(initialSize.Y, workSize.Y);
        Gui.SetNextWindowSize(initialSize, ImGuiCond.Appearing);
        Gui.PushFont(Gui.GetFont(), 15);
        Gui.PushStyleVar(ImGuiStyleVar.WindowPadding, UiScale(20, 20));
        var visible = Gui.BeginPopupModal(EditorLocalization.Widget("Settings", "Title"), ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        Gui.PopStyleVar();
        if (!visible) { Gui.PopFont(); return; }

        var bodyHeight = Math.Max(Gui.GetFrameHeight(), Gui.GetContentRegionAvail().Y - Gui.GetFrameHeight() - UiScale(20) - Gui.GetStyle().ItemSpacing.Y * 2);
        Gui.BeginChild("##SettingsBody", new System.Numerics.Vector2(0, bodyHeight), ImGuiChildFlags.None,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (Gui.BeginTable("##SettingsPanesV2", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV
            | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings))
        {
            Gui.TableSetupColumn("Categories", ImGuiTableColumnFlags.WidthStretch, 1f, 0u);
            Gui.TableSetupColumn("Properties", ImGuiTableColumnFlags.WidthStretch, 2f, 0u);
            Gui.TableNextRow(); Gui.TableSetColumnIndex(0);
            var paneHeight = Math.Max(Gui.GetFrameHeight(), bodyHeight - Gui.GetStyle().CellPadding.Y * 2);
            Gui.BeginChild("Settings categories", new System.Numerics.Vector2(0, paneHeight), ImGuiChildFlags.Borders);
            if (DrawNativeSelectable(EditorLocalization.Widget("Environment", "Settings"), settingsCategory == 6)) settingsCategory = 6;
            if (DrawNativeSelectable(EditorLocalization.Widget("External tools", "Settings"), settingsCategory == 3)) settingsCategory = 3;
            if (DrawNativeSelectable(EditorLocalization.Widget("Interface", "Settings"), settingsCategory == 0)) settingsCategory = 0;
            RecordSettingsParityControl("category/Interface");
            if (DrawNativeSelectable(EditorLocalization.Widget("Logging", "Settings"), settingsCategory == 5)) settingsCategory = 5;
            if (DrawNativeSelectable(EditorLocalization.Widget("Remote", "Settings"), settingsCategory == 11)) settingsCategory = 11;
            RecordSettingsParityControl("category/Remote");
            var sceneFlags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth;
            if (settingsCategory == 1) sceneFlags |= ImGuiTreeNodeFlags.Selected;
            var sceneExpanded = Gui.TreeNodeEx(EditorLocalization.Widget("Scene editor", "Settings"), sceneFlags);
            if (Gui.IsItemClicked() && !Gui.IsItemToggledOpen()
                || Gui.IsItemFocused() && (Gui.IsKeyPressed(ImGuiKey.Enter) || Gui.IsKeyPressed(ImGuiKey.Space))) settingsCategory = 1;
            if (sceneExpanded)
            {
                if (DrawNativeSelectable(EditorLocalization.Widget("Key bindings", "Settings"), settingsCategory == 9)) settingsCategory = 9;
                if (DrawNativeSelectable(EditorLocalization.Widget("Viewport settings", "Settings"), settingsCategory == 4)) settingsCategory = 4;
                Gui.TreePop();
            }
            if (DrawNativeSelectable(EditorLocalization.Widget("Themes", "Settings"), settingsCategory == 2)) settingsCategory = 2;
            if (DrawNativeSelectable(EditorLocalization.Widget("Tools", "Settings"), settingsCategory == 7)) settingsCategory = 7;
            if (DrawNativeSelectable(EditorLocalization.Widget("Script editor", "Settings"), settingsCategory == 8)) settingsCategory = 8;
            if (DrawNativeSelectable(EditorLocalization.Widget("UI editor", "Settings"), settingsCategory == 10)) settingsCategory = 10;
            ImGuiScopes.EndChild();
            Gui.TableSetColumnIndex(1);
            Gui.BeginChild("Settings values", new System.Numerics.Vector2(0, paneHeight), ImGuiChildFlags.Borders);
            DrawSettingsProperties();
            if (preferences?.IsBlocked == true)
            {
                DrawSelectableText("##PreferencesError", preferences.Error ?? "Invalid editor preferences.");
                if (Gui.Button(EditorLocalization.Widget("Reset")))
                {
                    if (preferences.RecoverDefaults())
                    { LoadSettingsDraft(); RestorePanelVisibility(preferences.Current); configuredGizmos = null; editorSettingsError = null; }
                    else editorSettingsError = preferences.Error;
                }
            }
            if (editorSettingsError != null) DrawSelectableText("##SettingsError", editorSettingsError);
            ImGuiScopes.EndChild();
            Gui.EndTable();
        }
        ImGuiScopes.EndChild();
        Gui.Dummy(UiScale(0, 20));
        var buttonWidth = Math.Max(Gui.CalcTextSize(EditorLocalization.Text("Save and close", "Button")).X,
            Gui.CalcTextSize(EditorLocalization.Text("Cancel", "Button")).X) + UiScale(40);
        var footerWidth = buttonWidth * 2 + UiScale(10);
        Gui.SetCursorPosX(Gui.GetCursorPosX() + Math.Max(0, Gui.GetContentRegionAvail().X - footerWidth));
        Gui.BeginDisabled(preferences == null || preferences.IsBlocked);
        if (Gui.Button(EditorLocalization.Widget("Save and close", "Button"), new System.Numerics.Vector2(buttonWidth, 0)) && SaveSettingsDraft()) Gui.CloseCurrentPopup();
        RecordSettingsParityControl("save");
        Gui.EndDisabled(); Gui.SameLine(0, UiScale(10));
        var cancel = Gui.Button(EditorLocalization.Widget("Cancel", "Button"), new System.Numerics.Vector2(buttonWidth, 0));
        RecordSettingsParityControl("cancel");
        if (cancel || Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && Gui.IsKeyPressed(ImGuiKey.Escape)) Gui.CloseCurrentPopup();
        Gui.EndPopup();
        Gui.PopFont();
    }

    private void DrawSettingsProperties()
    {
        var labelWidth = Math.Min(UiScale(310), Gui.GetContentRegionAvail().X * 0.55f);
        if (!Gui.BeginTable("##SettingsPropertiesV2", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings)) return;
        Gui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed, labelWidth);
        Gui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
        if (settingsCategory == 0)
        {
            DrawLanguageSetting();
            DrawSettingsBoolean("Ask before saving new scripts", ref settingsAskBeforeSavingNewScripts);
            DrawSettingsBoolean("Ask before deleting assets", ref settingsAskBeforeDeletingAssets);
            DrawSettingsBoolean("Ask before reloading assemblies", ref settingsAskBeforeReloadingAssemblies);
            DrawSettingsBoolean("Automatically reload assemblies", ref settingsAutoReloadAssemblies);
            DrawSettingsBoolean("Automatically reload last session at startup", ref settingsReloadSession);
            BeginSettingsProperty("Reset Game Studio layout");
            var resetWidth = Math.Min(UiScale(80), Gui.GetContentRegionAvail().X);
            Gui.SetCursorPosX(Gui.GetCursorPosX() + Math.Max(0, Gui.GetContentRegionAvail().X - resetWidth));
            if (Gui.Button(EditorLocalization.Widget("Reset"), new System.Numerics.Vector2(resetWidth, 0))) settingsResetLayout = true;
            Gui.PopID();
            DrawDefaultSessionSetting();
        }
        else if (settingsCategory == 1)
            DrawSettingsBoolean("Ask before deleting entities", ref settingsAskBeforeDeletingEntities);
        else if (settingsCategory == 10)
            DrawSettingsBoolean("Ask before deleting UI elements", ref settingsAskBeforeDeletingUIElements);
        else if (settingsCategory == 9) DrawCameraBindingSettings();
        else if (settingsCategory == 4)
        {
            DrawSettingsFloat("Default snap angle for rotation", ref settingsRotationSnap);
            DrawSettingsFloat("Default snap distance for translation", ref settingsTranslationSnap);
            DrawSettingsFloat("Default snap factor for scale", ref settingsScaleSnap);
            DrawSettingsBoolean("Display direction names instead of XYZ components", ref settingsDisplayDirectionNames);
            DrawSettingsBoolean("Use linear movement for the rotation gizmo", ref settingsLinearMovementForRotation);
        }
        else if (settingsCategory == 3)
        {
            DrawExternalEditorSetting("Default text editor", ref settingsTextEditor);
            DrawExternalEditorSetting("Shader editor", ref settingsShaderEditor);
            BeginSettingsProperty("Default IDE");
            Gui.SetNextItemWidth(-1);
            if (BeginNativeCombo("##Value", settingsIde))
            {
                foreach (var ide in ProjectIdeLauncher.Available)
                    if (DrawNativeSelectable(ide.DisplayName, ide.DisplayName == settingsIde)) settingsIde = ide.DisplayName;
                Gui.EndCombo();
            }
            Gui.PopID();
        }
        else if (settingsCategory == 5) DrawDebugOutputLevelSetting();
        else if (settingsCategory == 6) DrawGraphicsApiSetting();
        else if (settingsCategory == 7) DrawSettingsBoolean("Use effect compiler server for mobile platforms", ref settingsUseEffectCompilerServer);
        else if (settingsCategory == 8)
        {
            BeginSettingsProperty("Font size"); Gui.SetNextItemWidth(-1);
            Gui.InputInt("##Value", ref settingsScriptFontSize);
            settingsScriptFontSize = Math.Clamp(settingsScriptFontSize, 8, 72); Gui.PopID();
        }
        else if (settingsCategory == 11) DrawRemoteSettingsDraft();
        else
        {
            BeginSettingsProperty("Theme Name");
            Gui.SetNextItemWidth(-1);
            Gui.Combo("##Value", ref settingsTheme, EditorThemes.Names, EditorThemes.Names.Length);
            Gui.PopID();
        }
        Gui.EndTable();
    }

    private void BeginSettingsProperty(string label)
    {
        Gui.TableNextRow(); Gui.TableSetColumnIndex(0); Gui.AlignTextToFramePadding();
        Gui.PushTextWrapPos(0);
        try { DrawNativeLabel(EditorLocalization.Text(label, "Settings")); }
        finally { Gui.PopTextWrapPos(); }
        Gui.TableSetColumnIndex(1); Gui.PushID(label);
    }

    private void DrawSettingsBoolean(string label, ref bool value)
    {
        BeginSettingsProperty(label); DrawNativeCheckbox("##Value", ref value); Gui.PopID();
    }

    private void DrawSettingsFloat(string label, ref float value)
    {
        BeginSettingsProperty(label); Gui.SetNextItemWidth(-1);
        Gui.InputFloat("##Value", ref value, 0, 0, "%.3f"); Gui.PopID();
    }

    private bool SaveSettingsDraft()
    {
        if (preferences == null) return false;
        if (settingsRemotePort is < 1 or > 65535)
        { editorSettingsError = "The remote port must be between 1 and 65535."; return false; }
        if (!float.IsFinite(settingsTranslationSnap) || settingsTranslationSnap <= 0
            || !float.IsFinite(settingsRotationSnap) || settingsRotationSnap <= 0
            || !float.IsFinite(settingsScaleSnap) || settingsScaleSnap <= 0)
        { editorSettingsError = "Snap values must be greater than zero."; return false; }
        string defaultSessionPath;
        try { defaultSessionPath = string.IsNullOrWhiteSpace(settingsDefaultSessionPath) ? "" : Path.GetFullPath(settingsDefaultSessionPath.Trim()); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { editorSettingsError = error.Message; return false; }
        if (string.IsNullOrWhiteSpace(settingsTextEditor) || string.IsNullOrWhiteSpace(settingsShaderEditor))
        { editorSettingsError = "The path to the text editor in Settings is empty."; return false; }
        var previous = preferences.Current;
        var startupChanged = previous.DefaultSessionPath != defaultSessionPath || previous.ReloadLastSession != settingsReloadSession;
        if (startupChanged && !PublishProjectStartupSettings(defaultSessionPath, settingsReloadSession)) return false;
        if (!preferences.TryUpdate(current =>
        {
            current.AskBeforeDeletingAssets = settingsAskBeforeDeletingAssets;
            current.DefaultSessionPath = defaultSessionPath;
            if (startupChanged) current.DefaultSessionEditorVersion = Stride.GameStudio.Helpers.StrideGameStudio.EditorVersion;
            current.UseLinearMovementForRotation = settingsLinearMovementForRotation;
            current.AskBeforeSavingNewScripts = settingsAskBeforeSavingNewScripts;
            current.ScriptEditorFontSize = settingsScriptFontSize;
            current.AskBeforeReloadingAssemblies = settingsAskBeforeReloadingAssemblies;
            current.AutoReloadAssemblies = settingsAutoReloadAssemblies;
            current.CameraBindings = settingsCameraBindings.Clone(); current.InvertPanningAxis = settingsInvertPanningAxis;
            current.DisplayDirectionNames = settingsDisplayDirectionNames;
            current.DefaultIDE = settingsIde;
            current.DefaultTextEditor = settingsTextEditor; current.ShaderEditor = settingsShaderEditor;
            current.DebugOutputLevel = settingsDebugOutputLevel;
            current.GraphicsApi = settingsGraphicsApi;
            current.UseEffectCompilerServer = settingsUseEffectCompilerServer;
            current.Language = settingsLanguage;
            current.RemoteHost = settingsRemoteHost; current.RemotePort = settingsRemotePort;
            current.RemoteUsername = settingsRemoteUsername; current.RemotePassword = settingsRemotePassword;
            current.RemoteLocation = settingsRemoteLocation; current.RemoteDisplay = settingsRemoteDisplay;
            current.AskForRemoteCredentials = settingsAskForRemoteCredentials;
            current.Theme = (EditorTheme)settingsTheme; current.ReloadLastSession = settingsReloadSession;
            current.DefaultTranslationSnap = settingsTranslationSnap;
            current.DefaultRotationSnap = settingsRotationSnap; current.DefaultScaleSnap = settingsScaleSnap;
            current.AskBeforeDeletingEntities = settingsAskBeforeDeletingEntities;
            current.AskBeforeDeletingUIElements = settingsAskBeforeDeletingUIElements;
        }))
        {
            editorSettingsError = startupChanged ? "Native startup settings were saved, but local preferences could not be saved: " + preferences.Error : preferences.Error;
            return false;
        }
        var saved = preferences.Current;
        if (previous.GraphicsApi != saved.GraphicsApi || previous.UseEffectCompilerServer != saved.UseEffectCompilerServer
            || previous.Language != saved.Language) settingsRestartNotice = true;
        diagnosticRouting?.Apply(saved);
        if (settingsResetLayout) { resetLayout = true; ResetPanelVisibility(); }
        if (previous.DefaultTranslationSnap != saved.DefaultTranslationSnap
            || previous.DefaultRotationSnap != saved.DefaultRotationSnap || previous.DefaultScaleSnap != saved.DefaultScaleSnap)
            configuredGizmos = null;
        return true;
    }

    private void PumpSceneEditorPreferences()
    {
        var current = preferences?.Current ?? FallbackSceneEditorPreferences;
        if (engineViewport?.CameraOrientation is { } orientation)
            orientation.DisplayDirectionNames = current.DisplayDirectionNames;
        if (engineViewport?.Camera is { } camera) camera.InvertPanningAxis = current.InvertPanningAxis;
        var gizmos = engineViewport?.Gizmos;
        if (gizmos != null && gizmos.DynamicSnapDown == null) gizmos.DynamicSnapDown = IsDynamicSceneSnapDown;
        if (gizmos != null) gizmos.LinearMovementForRotation = current.UseLinearMovementForRotation;
        if (gizmos == null || ReferenceEquals(configuredGizmos, gizmos)) return;
        configuredGizmos = gizmos;
        gizmos.TranslationSnap = current.DefaultTranslationSnap;
        gizmos.RotationSnap = current.DefaultRotationSnap; gizmos.ScaleSnap = current.DefaultScaleSnap;
    }
}
