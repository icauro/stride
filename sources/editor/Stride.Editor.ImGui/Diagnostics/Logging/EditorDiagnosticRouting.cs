using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

/// <summary>Owns debugger output routing independently of the Output window's capture.</summary>
internal sealed class EditorDiagnosticRouting : IDisposable
{
    private readonly DebugLogListener debugger = new();
    private readonly HashSet<string> sourceModules = new(StringComparer.Ordinal);
    private bool disposed;

    public EditorDiagnosticRouting(EditorPreferences settings)
    {
        Apply(settings);
        GlobalLogger.GlobalMessageLogged += debugger;
    }

    public void Apply(EditorPreferences settings)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var defaults = new EditorPreferences();
        var source = defaults.SourceModuleLevels;
        foreach (var pair in settings.SourceModuleLevels) source[pair.Key] = pair.Value;
        foreach (var module in sourceModules.Where(module => !source.ContainsKey(module)))
            GlobalLogger.SetModuleLevel(module, Logger.MinimumLevelEnabled);
        foreach (var pair in source) GlobalLogger.SetModuleLevel(pair.Key, pair.Value);
        sourceModules.Clear(); sourceModules.UnionWith(source.Keys);
        debugger.MinimumLevel = settings.DebugOutputLevel;
        var levels = defaults.DebugOutputModuleLevels;
        foreach (var pair in settings.DebugOutputModuleLevels) levels[pair.Key] = pair.Value;
        foreach (var module in debugger.ModuleLevels.Keys)
            if (!levels.ContainsKey(module)) debugger.ModuleLevels.TryRemove(module, out _);
        foreach (var pair in levels) debugger.ModuleLevels[pair.Key] = pair.Value;
    }

    public void Dispose()
    {
        if (disposed) return;
        GlobalLogger.GlobalMessageLogged -= debugger;
        debugger.Dispose(); disposed = true;
    }
}
