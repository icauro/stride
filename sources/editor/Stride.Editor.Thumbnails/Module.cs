using Stride.Core;
using Stride.Core.Reflection;

namespace Stride.Editor.Thumbnails;

internal static class Module
{
    [ModuleInitializer]
    internal static void Initialize()
        => AssemblyRegistry.Register(typeof(Module).Assembly, AssemblyCommonCategories.Assets);
}
