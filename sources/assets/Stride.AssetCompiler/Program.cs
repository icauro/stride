// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.IO;

namespace Stride.AssetCompiler
{
    class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 2 && args[0] == "preview-texture") return TexturePreviewCompiler.Run(args[1]);
                if (args.Length == 2 && args[0] == "preview-sprite-sheet") return SpriteSheetPreviewCompiler.Run(args[1]);
                if (args.Length == 2 && args[0] == "preview-skybox") return SkyboxPreviewCompiler.Run(args[1]);
                if (args.Length == 2 && args[0] == "preview-font") return FontPreviewCompiler.Run(args[1]);
                if (args.Length == 2 && args[0] == "preview-media")
                {
                    // This entry bypasses PackageBuilderApp, whose ordinary
                    // path registers MSBuild before constructing a native session.
                    Stride.Core.Assets.PackageSessionPublicHelper.FindAndSetMSBuildVersion();
                    return MediaPreviewCompiler.Run(args[1]);
                }
                if (args.Length == 2 && args[0] == "preview-thumbnail") return NativeThumbnailPreviewCompiler.Run(args[1]);
                if (args.Length == 2 && args[0] == "preview-thumbnail-session") return NativeThumbnailWorkerServer.Run(args[1]);
                if (args.Length == 2 && args[0] == "probe-media") return AssetMediaProbeCompiler.Run(args[1]);
                if (args.Length == 2 && args[0] == "import-source") return AssetSourceImporter.Run(args[1]);
                // Running first time? If yes, create nuget redirect package.
                //var packageVersion = new PackageVersion(StrideVersion.NuGetVersion);
                //if (PackageStore.Instance.IsDevelopmentStore)
                //{
                //    PackageStore.Instance.CheckDeveloperTargetRedirects("Stride", packageVersion, PackageStore.Instance.InstallationPath).Wait();
                //}

                var packageBuilder = new PackageBuilderApp();
                var returnValue =  packageBuilder.Run(args);

                return returnValue;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Unexpected exception in AssetCompiler: {0}", ex);
                return 1;
            }
            finally
            {
                // Free all native library loaded from the process
                // We cannot free native libraries are some of them are loaded from static module initializer
                // NativeLibrary.UnLoadAll();
            }
        }
    }
}
