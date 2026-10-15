namespace Stride.Editor.ImGui;

using Stride.Core.Diagnostics;

internal sealed record AssetFilterPreference(string Category, string Value, bool Enabled);

internal sealed class EditorPreferences
{
    public int Version { get; set; } = 1;
    public EditorWindowPlacement? WindowPlacement { get; set; }
    public bool ShowAssetPreview { get; set; } = true;
    public List<AssetFilterPreference> AssetFilters { get; set; } = [];
    public List<AssetFilterPreference>? ReferenceFilters { get; set; }
    public AssetBrowserSort AssetSort { get; set; } = AssetBrowserSort.TypeThenName;
    public bool AssetIncludeDescendants { get; set; }
    public AssetBrowserDisplayMode? AssetDisplayMode { get; set; }
    public bool AssetTileView { get; set; } = true;
    public int AssetTileThumbnailSize { get; set; } = 96;
    public int AssetGridThumbnailSize { get; set; } = 16;
    public bool ShowHierarchy { get; set; } = true;
    public bool ShowInspector { get; set; } = true;
    public bool ShowAssets { get; set; } = true;
    public bool ShowSolutionExplorer { get; set; } = true;
    public bool ShowOutput { get; set; } = true;
    public bool ShowReferences { get; set; } = true;
    public bool ShowAssetErrors { get; set; } = true;
    public bool ShowEditHistory { get; set; } = true;
    public string DefaultIDE { get; set; } = "Default IDE";
    public string GraphicsApi { get; set; } = "Default";
    public Stride.Core.Assets.Editor.SupportedLanguage Language { get; set; }
    public string DefaultTextEditor { get; set; } = @"%windir%\system32\notepad.exe";
    public string ShaderEditor { get; set; } = @"%windir%\system32\notepad.exe";
    public LogMessageType DebugOutputLevel { get; set; } = LogMessageType.Warning;
    public Dictionary<string, LogMessageType> DebugOutputModuleLevels { get; set; } = new(StringComparer.Ordinal)
    { ["GraphicsDevice"] = LogMessageType.Debug, ["GraphicsDebug"] = LogMessageType.Debug };
    public Dictionary<string, LogMessageType> SourceModuleLevels { get; set; } = new(StringComparer.Ordinal)
    {
        ["AssetBuilderService"] = LogMessageType.Debug, ["EffectCompilerCache"] = LogMessageType.Debug,
        ["Preview"] = LogMessageType.Debug, ["Quantum"] = LogMessageType.Debug,
    };
    public EditorTheme Theme { get; set; } = EditorTheme.ExpressionDark;
    public bool ReloadLastSession { get; set; }
    public string DefaultSessionPath { get; set; } = "";
    public string? DefaultSessionEditorVersion { get; set; }
    public bool AskBeforeSavingNewScripts { get; set; } = true;
    public int ScriptEditorFontSize { get; set; } = 12;
    public bool AskBeforeDeletingAssets { get; set; } = true;
    public bool AskBeforeDeletingEntities { get; set; } = true;
    public bool AskBeforeDeletingUIElements { get; set; } = true;
    public bool AskBeforeReloadingAssemblies { get; set; } = true;
    public bool AutoReloadAssemblies { get; set; } = true;
    public bool UseEffectCompilerServer { get; set; }
    public CameraKeyBindings CameraBindings { get; set; } = new();
    public bool InvertPanningAxis { get; set; } = true;
    public bool DisplayDirectionNames { get; set; }
    public bool UseLinearMovementForRotation { get; set; }
    public float DefaultTranslationSnap { get; set; } = 1;
    public float DefaultRotationSnap { get; set; } = 22.5f;
    public float DefaultScaleSnap { get; set; } = 1.1f;
    public string? LastProjectPath { get; set; }
    public List<string> RecentProjects { get; set; } = [];
    public Dictionary<string, string> ActiveProjects { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> BuildTargetPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> BuildConfigurations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> DialogDirectories { get; set; } = new(StringComparer.Ordinal);
    public string RemoteHost { get; set; } = "";
    public int RemotePort { get; set; } = 22;
    public string RemoteUsername { get; set; } = "";
    public string RemotePassword { get; set; } = "";
    public string RemoteLocation { get; set; } = "Projects";
    public string RemoteDisplay { get; set; } = ":0.0";
    public bool AskForRemoteCredentials { get; set; } = true;
}
