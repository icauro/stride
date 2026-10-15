using Stride.Shaders.Compiler;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void ConfigureEditorShaderSources()
    {
        IEffectCompiler compiler = EffectSystem.Compiler;
        while (compiler is EffectCompilerChain chain) compiler = chain.InnerCompiler;
        if (compiler is not EffectCompiler local) return;

        var sources = Path.Combine(AppContext.BaseDirectory, "Shaders", "Engine");
        if (!Directory.Exists(sources))
            throw new DirectoryNotFoundException("The editor shader source directory is missing: " + sources);
        var directories = Directory.EnumerateFiles(sources, "*.sdsl", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (directories.Length == 0)
            throw new InvalidDataException("The editor shader source directory contains no shaders: " + sources);
        local.UseFileSystem = true;
        local.SourceDirectories.Clear();
        local.SourceDirectories.AddRange(directories);
    }
}
