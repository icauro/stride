using System.Reflection;
using Stride.Core.Assets;

namespace Stride;

internal static class EditorServiceGraphicsBootstrap
{
    [Stride.Core.ModuleInitializer(int.MinValue)]
    internal static void Initialize()
    {
        // A router reference loaded inside the editor must not reconfigure its host.
        if (Assembly.GetEntryAssembly() != typeof(EditorServiceGraphicsBootstrap).Assembly) return;
        GraphicsApiHostResolver.Setup(GraphicsApiSelector.Resolve());
    }
}
