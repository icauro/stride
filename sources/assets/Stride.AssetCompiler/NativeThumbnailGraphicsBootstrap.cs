using System.Reflection;
using Stride.Core;
using Stride.Core.Assets;

namespace Stride.AssetCompiler;

internal static class NativeThumbnailGraphicsBootstrap
{
    [ModuleInitializer(int.MinValue)]
    internal static void Initialize()
    {
        if (Assembly.GetEntryAssembly() != typeof(NativeThumbnailGraphicsBootstrap).Assembly) return;
        GraphicsApiHostResolver.Setup(GraphicsApiSelector.Resolve());
    }
}
