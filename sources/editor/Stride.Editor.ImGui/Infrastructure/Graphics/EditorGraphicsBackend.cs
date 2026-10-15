using System.Runtime.CompilerServices;
using System.Text.Json;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

/// <summary>Resolve one engine graphics implementation before loading the editor game.</summary>
internal static class EditorGraphicsBackend
{
    private static readonly string[] SupportedApis = ["Direct3D11", "Direct3D12", "Vulkan"];
    public static string SelectedApi { get; private set; } = "Direct3D11";
    private static bool? initializationResult;
    private static bool resolverInitialized;

    // The project-local build target places this call before the generated assembly
    // scan, which can resolve graphics types before ordinary module initializers run.
    internal static void InitializeResolverBeforeAssemblyScan()
    {
        if (!InitializeResolver(Environment.GetCommandLineArgs().Skip(1).ToArray()))
            Environment.Exit(0); // User declined the existing unavailable-preference fallback.
    }
    [Stride.Core.ModuleInitializer(-100000)]
    internal static void InitializeBeforeGeneratedSerializers()
        => Initialize(Environment.GetCommandLineArgs().Skip(1).ToArray());
    public static bool Initialize(string[] args)
    {
        if (initializationResult is { } initialized) return initialized;
        var result = InitializeCore(args);
        initializationResult = result;
        return result;
    }
    private static bool InitializeCore(string[] args)
    {
        if (!InitializeResolver(args)) return false;
        if (args.Contains("--graphics-capabilities", StringComparer.OrdinalIgnoreCase))
        {
            var staged = SupportedApis.Where(api => File.Exists(Path.Combine(AppContext.BaseDirectory, api, "Stride.Graphics.dll"))).ToArray();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                hostPlatform = "Windows", selectedApi = SelectedApi,
                sourceApis = SupportedApis, stagedApis = staged,
                flatGraphicsAssembly = File.Exists(Path.Combine(AppContext.BaseDirectory, "Stride.Graphics.dll")),
                rendering = "Stride.Graphics", detachedPresentation = "Win32/Stride swap chain",
                hardwareProbed = false,
            }, new JsonSerializerOptions { WriteIndented = true }));
            return false;
        }
        if (!ValidateLoadedApi(args)) return false;
        Environment.SetEnvironmentVariable("STRIDE_GRAPHICS_API", SelectedApi);
        return true;
    }
    // Only BCL, native startup dialog and host-local resolver code may run here.
    // In particular, never call ValidateLoadedApi until resolver hooks exist.
    private static bool InitializeResolver(string[] args)
    {
        if (resolverInitialized) return true;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The ImGui editor currently requires Windows native window, input and dialog services.");
        GraphicsApiSelector.HostPreference = EditorStartupPreferences.GraphicsApi(args);
        SelectedApi = GraphicsApiHostResolver.Setup(GraphicsApiSelector.Resolve());
        if (GraphicsApiSelector.StartupError is { } error) throw new InvalidOperationException(error);
        if (GraphicsApiHostResolver.UnavailablePreference is { } unavailable)
        {
            if (!WindowsStartupDialog.ConfirmGraphicsFallback(unavailable, GraphicsApiHostResolver.FallbackApi!)) return false;
            EditorStartupPreferences.ResetGraphicsApi(args);
            SelectedApi = GraphicsApiHostResolver.UseFallback();
        }
        resolverInitialized = true;
        return true;
    }
    // Keep graphics-dependent JIT resolution out of Initialize until resolver hooks exist.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ValidateLoadedApi(string[] args)
    {
        var loaded = Stride.Graphics.GraphicsDevice.Platform.ToString();
        if (!SupportedApis.Contains(loaded, StringComparer.Ordinal)) throw new NotSupportedException("The editor has no graphics renderer for " + loaded + ".");
        if (!string.Equals(loaded, SelectedApi, StringComparison.OrdinalIgnoreCase))
        {
            if (GraphicsApiSelector.Source == GraphicsApiSelector.SelectionSource.HostPreference)
            {
                if (!WindowsStartupDialog.ConfirmGraphicsFallback(SelectedApi, loaded)) return false;
                EditorStartupPreferences.ResetGraphicsApi(args); SelectedApi = loaded;
                return true;
            }
            if (GraphicsApiSelector.Source != GraphicsApiSelector.SelectionSource.None)
                throw new InvalidOperationException("Graphics API '" + SelectedApi + "' was requested, but this flat build contains '" + loaded + "'. Deploy the requested API variant.");
            SelectedApi = loaded;
        }
        return true;
    }
}
