using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Stride.Assets.Textures;
using Stride.Core.Assets;
using Stride.Core.BuildEngine;
using Stride.Core.Diagnostics;

namespace Stride.AssetCompiler;

/// <summary>Runs the regular texture import command against an isolated authored snapshot.</summary>
internal static class TexturePreviewCompiler
{
    public static int Run(string requestPath)
    {
        var request = JsonSerializer.Deserialize<TexturePreviewBuildParameters>(File.ReadAllText(requestPath))
            ?? throw new ArgumentException("Invalid texture preview request.");
        var asset = AssetFileSerializer.Load<TextureAsset>(request.AssetSnapshot).Asset;
        var logger = new LoggerResult();
        logger.MessageLogged += (_, args) => Console.WriteLine(args.Message);
        var package = new Package { FullPath = Path.Combine(request.OutputDirectory, "preview.sdpkg") };
        using var session = new PackageSession(package);
        Directory.CreateDirectory(request.BuildDirectory); Directory.CreateDirectory(request.OutputDirectory);
        var parameters = new TextureConvertParameters(request.SourcePath, asset, request.Platform, request.GraphicsPlatform,
            request.GraphicsProfile, request.TextureQuality, request.ColorSpace);
        const string indexName = "index.imgui-texture-preview";
        using var builder = new Builder(logger, request.BuildDirectory, indexName) { ThreadCount = 1 };
        builder.Root.Add(new TextureAssetCompiler.TextureConvertCommand(request.Url, parameters, package));
        var result = builder.Run(Builder.Mode.Build);
        if (result != BuildResultCode.Successful) return (int)result;
        builder.WriteIndexFile(false); builder.WriteClosureFile();
        new BundlePacker().Build(logger, session, package, indexName, request.OutputDirectory,
            builder.DisableCompressionIds, false, new List<string>());
        return logger.HasErrors ? (int)BuildResultCode.BuildError : 0;
    }
}
