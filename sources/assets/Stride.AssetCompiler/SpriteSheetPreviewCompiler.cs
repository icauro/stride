using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Stride.Assets;
using Stride.Assets.Sprite;
using Stride.Assets.Textures;
using Stride.Core.Assets;
using Stride.Core.Assets.Compiler;
using Stride.Core.BuildEngine;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Graphics;

namespace Stride.AssetCompiler;

/// <summary>Compiles retained sheet settings through the production atlas/compiler path.</summary>
internal static class SpriteSheetPreviewCompiler
{
    public static int Run(string requestPath)
    {
        var request = JsonSerializer.Deserialize<SpriteSheetPreviewBuildParameters>(File.ReadAllText(requestPath))
            ?? throw new ArgumentException("Invalid sprite-sheet preview request.");
        var asset = AssetFileSerializer.Load<SpriteSheetAsset>(request.AssetSnapshot).Asset;
        var assetDirectory = Path.GetDirectoryName(Path.GetFullPath(request.AssetPath))!;
        // The snapshot is stored outside the user project. Keep the real source origin,
        // including relative sources when packed commands read SpriteInfo.Source directly.
        foreach (var sprite in asset.Sprites)
            if (!string.IsNullOrEmpty(sprite.Source))
                sprite.Source = new UFile(Path.GetFullPath(sprite.Source, assetDirectory));
        var logger = new LoggerResult();
        logger.MessageLogged += (_, args) => Console.WriteLine(args.Message);
        var location = new UFile(request.Url);
        if (location.HasDrive || string.IsNullOrEmpty(request.Url)) throw new ArgumentException("Invalid preview asset URL.");
        string? assetNamespace = null;
        if (location.IsAbsolute)
        {
            var separator = request.Url.IndexOf('/', 1);
            if (!request.Url.StartsWith("/", StringComparison.Ordinal) || separator <= 1 || separator == request.Url.Length - 1)
                throw new ArgumentException("The preview asset URL has no native namespace and relative location.");
            assetNamespace = request.Url.Substring(1, separator - 1);
            location = new StandalonePackage(new Package()) { AssetNamespace = assetNamespace }.Unqualify(location);
            if (location.IsAbsolute || location.HasDrive) throw new ArgumentException("The preview asset namespace cannot resolve its relative location.");
        }
        var package = new Package { FullPath = Path.Combine(request.OutputDirectory, "preview.sdpkg"), AssetNamespace = assetNamespace };
        using var session = new PackageSession(package);
        var item = new AssetItem(location, asset) { AlternativePath = new UFile(request.AssetPath) };
        package.Assets.Add(item);
        if (!string.Equals(item.Location.ToString(), request.Url, StringComparison.Ordinal))
            throw new ArgumentException("The isolated preview package changed the compiled runtime URL.");
        var settings = GameSettingsFactory.Create();
        var rendering = settings.GetOrCreate<RenderingSettings>();
        rendering.DefaultGraphicsProfile = request.GraphicsProfile;
        rendering.ColorSpace = request.ColorSpace;
        settings.GetOrCreate<TextureSettings>().TextureQuality = request.TextureQuality;
        using var context = new AssetCompilerContext
        {
            Package = package, Platform = request.Platform, CompilationContext = typeof(AssetCompilationContext),
            BuildConfiguration = "Debug",
        };
        context.SetGameSettingsAsset(settings);
        context.OptionProperties["StrideGraphicsApi"] = request.GraphicsPlatform.ToString();
        var prepared = new SpriteSheetAssetCompiler().Prepare(context, item);
        foreach (var message in prepared.Messages) logger.Log(message);
        if (prepared.HasErrors) return (int)BuildResultCode.BuildError;
        Directory.CreateDirectory(request.BuildDirectory); Directory.CreateDirectory(request.OutputDirectory);
        const string indexName = "index.imgui-sprite-sheet-preview";
        using var builder = new Builder(logger, request.BuildDirectory, indexName) { ThreadCount = 1 };
        builder.Root.Add(prepared.BuildSteps);
        var result = builder.Run(Builder.Mode.Build);
        if (result != BuildResultCode.Successful) return (int)result;
        builder.WriteIndexFile(false); builder.WriteClosureFile();
        new BundlePacker().Build(logger, session, package, indexName, request.OutputDirectory,
            builder.DisableCompressionIds, false, new List<string>());
        return logger.HasErrors ? (int)BuildResultCode.BuildError : 0;
    }
}
