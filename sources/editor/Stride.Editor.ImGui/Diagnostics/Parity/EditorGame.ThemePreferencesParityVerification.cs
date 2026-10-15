using System.Numerics;
using System.Text.Json;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed class ThemePreferencesParityRun
{
    internal required EditorPreferencesStore Store;
    internal required EditorPreferencesStore? Previous;
    internal required string Path;
    internal required byte[] BaselineBytes;
    internal required EditorPreferences Baseline;
    internal ImGuiStyle LiveStyle, UnscaledStyle;
    internal EditorTheme AppliedTheme;
    internal float Dpi;
    internal int ThemeRevision, Frame, Phase, Round;
    internal bool RestartNotice, ResetLayout;
    internal string? SettingsError;
    internal readonly List<object> Checks = [];
    internal bool Retired;
}

internal sealed partial class EditorGame
{
    internal unsafe ThemePreferencesParityRun BeginThemePreferencesParityVerification(string ownedDirectory)
    {
        var root = Environment.GetEnvironmentVariable("STRIDE_EDITOR_TEST_ARTIFACTS");
        if (!args.Contains("--ui-test") || root == null || workspace == null || IsAuthoringPipelineUpdating
            || openEditorSettings || SettingsUiParityModalOpen
            || !Path.GetFullPath(ownedDirectory).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Theme verification requires an idle private actor and no active Settings dialog.");
        Directory.CreateDirectory(ownedDirectory);
        var path = Path.Combine(ownedDirectory, "preferences.json");
        if (File.Exists(path)) throw new IOException("The theme fixture directory must be fresh.");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(preferences?.Current ?? new EditorPreferences { Theme = appliedTheme }));
        var store = new EditorPreferencesStore(path); store.Load();
        if (store.IsBlocked) throw new IOException(store.Error);
        var run = new ThemePreferencesParityRun { Store = store, Previous = preferences, Path = path,
            BaselineBytes = File.ReadAllBytes(path), Baseline = store.Current,
            LiveStyle = *Gui.GetStyle().Handle, UnscaledStyle = unscaledStyle, AppliedTheme = appliedTheme,
            Dpi = editorDpiScale, ThemeRevision = appliedThemeRevision, Frame = Gui.GetFrameCount(),
            RestartNotice = settingsRestartNotice, ResetLayout = resetLayout, SettingsError = editorSettingsError };
        preferences = store;
        LoadSettingsDraft();
        settingsTheme = (int)(appliedTheme == EditorTheme.LightSteelBlue ? EditorTheme.DarkSteel : EditorTheme.LightSteelBlue);
        return run;
    }

    internal unsafe object? StepThemePreferencesParityVerification(ThemePreferencesParityRun run)
    {
        if (run.Retired || !ReferenceEquals(preferences, run.Store))
            throw new InvalidOperationException("The private theme preferences owner changed.");
        var frame = Gui.GetFrameCount();
        if (frame <= run.Frame) return null;
        run.Frame = frame;
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        Require(editorDpiScale == run.Dpi && run.Dpi > 0,
            "This theme case is scoped to one actual stable DPI; monitor transitions require their separate acceptance.");
        var style = Gui.GetStyle();
        if (run.Phase == 0)
        {
            Require(ReferenceEquals(run.Store.Current, run.Baseline) && appliedTheme == run.AppliedTheme
                && File.ReadAllBytes(run.Path).AsSpan().SequenceEqual(run.BaselineBytes)
                && style.Colors[(int)ImGuiCol.WindowBg] == run.LiveStyle.Colors[(int)ImGuiCol.WindowBg],
                "An uncommitted theme draft must not change native colors or published/durable preferences.");
            LoadSettingsDraft();
            Require(settingsTheme == (int)run.Baseline.Theme,
                "Reopening the genuine settings draft must discard an uncommitted theme choice.");
            run.Checks.Add(new { contract = "uncommitted-draft-discard", unchangedPaletteAndBytes = true });
            run.Phase = 1;
            return null;
        }
        EditorTheme[] themes = [EditorTheme.DarkSteel, EditorTheme.Divided, EditorTheme.LightSteelBlue,
            EditorTheme.ExpressionDark, EditorTheme.DarkSteel, EditorTheme.DarkSteel];
        var requested = themes[run.Round];
        if (run.Phase == 1)
        {
            LoadSettingsDraft(); settingsTheme = (int)requested;
            Require(SaveSettingsDraft(), "The production Settings Save must durably publish the theme choice: " + editorSettingsError);
            run.Phase = 2;
            return null;
        }
        Require(appliedTheme == requested && run.Store.Current.Theme == requested,
            "Normal frame-boundary DPI/theme composition must adopt the actual saved theme enum.");
        var background = requested switch { EditorTheme.DarkSteel => 0x3C3C3Eu, EditorTheme.Divided => 0x383838u,
            EditorTheme.LightSteelBlue => 0xDBDBDBu, _ => 0x101113u };
        static Vector4 Color(uint rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1);
        Require(Vector4.DistanceSquared(style.Colors[(int)ImGuiCol.WindowBg], Color(background)) < 1e-10f
            && Vector4.DistanceSquared(style.Colors[(int)ImGuiCol.CheckMark], Color(0xDA0830)) < 1e-10f,
            "The actual composed native palette must expose the selected theme background and Stride accent.");
        var expected = run.UnscaledStyle; expected.ScaleAllSizes(run.Dpi);
        Require(style.WindowPadding == expected.WindowPadding && style.FramePadding == expected.FramePadding
            && style.ItemSpacing == expected.ItemSpacing && style.IndentSpacing == expected.IndentSpacing
            && style.ScrollbarSize == expected.ScrollbarSize && style.FontScaleDpi == run.Dpi,
            "Theme changes/repeated saves must scale a fresh baseline instead of compounding actual DPI metrics.");
        var reopened = new EditorPreferencesStore(run.Path); reopened.Load();
        Require(!reopened.IsBlocked && reopened.Current.Theme == requested,
            "Independent durable preference load must retain the exact saved theme enum.");
        run.Checks.Add(new { theme = requested.ToString(), nativeBackground = background, actualDpi = run.Dpi,
            persistedEnum = true, stableMetrics = true, frame });
        run.Round++; run.Phase = 1;
        if (run.Round < themes.Length) return null;
        return new { passed = true, checks = run.Checks.ToArray(),
            scope = "Production settings draft/Save and normal frame-boundary palette composition at one real DPI; draft discard service boundary, not a Cancel button gesture, exact WPF pixels or mixed-monitor coverage." };
    }

    internal unsafe void EndThemePreferencesParityVerification(ThemePreferencesParityRun run)
    {
        if (run.Retired) return;
        if (!ReferenceEquals(preferences, run.Store)) throw new InvalidOperationException("The theme fixture owner changed before restoration.");
        preferences = run.Previous;
        unscaledStyle = run.UnscaledStyle; appliedTheme = run.AppliedTheme;
        editorDpiScale = run.Dpi; appliedThemeRevision = run.ThemeRevision;
        viewportStylePolicy.SetBaseline(run.UnscaledStyle); viewportStylePolicy.Apply(run.Dpi);
        *Gui.GetStyle().Handle = run.LiveStyle;
        LoadSettingsDraft(); settingsRestartNotice = run.RestartNotice;
        resetLayout = run.ResetLayout; editorSettingsError = run.SettingsError;
        diagnosticRouting?.Apply(preferences?.Current ?? FallbackSceneEditorPreferences);
        File.WriteAllBytes(run.Path, run.BaselineBytes);
        if (!File.ReadAllBytes(run.Path).AsSpan().SequenceEqual(run.BaselineBytes))
            throw new IOException("The private theme baseline was not restored exactly.");
        run.Retired = true;
    }
}
