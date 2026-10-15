namespace Stride.Editor.ImGui;

internal static class AssetCompilerLocator
{
    internal static string GetExecutable()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "Stride.AssetCompiler.exe");
        if (File.Exists(executable)) return executable;
        var packaged = Path.Combine(AppContext.BaseDirectory, "tools", "asset-compiler", "Stride.AssetCompiler.exe");
        if (File.Exists(packaged)) return packaged;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null)
        {
            var development = Path.Combine(root.FullName, "sources", "assets", "Stride.AssetCompiler", "bin", "Release", "net10.0-windows", "Stride.AssetCompiler.exe");
            if (File.Exists(development)) return development;
            root = root.Parent;
        }
        throw new FileNotFoundException("The Stride asset compiler is missing. Build or install it beside the editor.", executable);
    }
}
