using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Stride.Assets;
using Stride.Assets.Effect;
using Stride.Assets.Skyboxes;
using Stride.Assets.Textures;
using Stride.Core.Assets;
using Stride.Core.Assets.Compiler;
using Stride.Core.BuildEngine;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Graphics;

namespace Stride.AssetCompiler;

internal static class SkyboxPreviewCompiler
{
    public static int Run(string requestPath)
    {
        var request = JsonSerializer.Deserialize<SkyboxPreviewBuildParameters>(File.ReadAllText(requestPath))
            ?? throw new ArgumentException("Invalid skybox preview request.");
        var logger = new LoggerResult(); logger.MessageLogged += (_, args) => Console.WriteLine(args.Message);
        var inputs = new[] { request.RootInput }.Concat(request.Textures.Select(dependency => dependency.Input))
            .Concat(request.Shaders.Select(dependency => dependency.Input)).ToArray();
        using var session = Stride.Assets.Media.MediaPreviewPackageSession.Create(inputs, request.OutputDirectory, logger);
        var root = session.FindAsset(new AssetId(Guid.Parse(request.RootInput.Id)))
            ?? throw new InvalidOperationException("The captured skybox root is missing.");
        var asset = root.Asset as SkyboxAsset ?? throw new InvalidOperationException("The captured skybox root has an incompatible type.");
        var package = root.Package;
        var settings = GameSettingsFactory.Create(); var rendering = settings.GetOrCreate<RenderingSettings>();
        rendering.DefaultGraphicsProfile = request.GraphicsProfile; rendering.ColorSpace = request.ColorSpace;
        settings.GetOrCreate<TextureSettings>().TextureQuality = request.TextureQuality;
        using var context = new AssetCompilerContext
        { Package = package, Platform = request.Platform, CompilationContext = typeof(AssetCompilationContext), BuildConfiguration = "Debug" };
        context.SetGameSettingsAsset(settings); context.OptionProperties["StrideGraphicsApi"] = request.GraphicsPlatform.ToString();
        // Production dependency analysis prepares the real SkyboxAssetCompiler,
        // texture imports, shader sources and the WPF preview's two GGX LUTs.
        var prepared = new AssetDependenciesCompiler(typeof(AssetCompilationContext)).PrepareMany(context, session.Packages.SelectMany(owner => owner.Assets).Where(item => item.Id == root.Id || item.Asset is EffectShaderAsset).ToList());
        foreach (var message in prepared.Messages) logger.Log(message);
        if (prepared.HasErrors) return (int)BuildResultCode.BuildError;
        var rootStep = prepared.BuildSteps.Steps.OfType<AssetBuildStep>().FirstOrDefault(step => step.AssetItem.Id == asset.Id)
            ?? throw new InvalidOperationException("The production skybox compiler did not prepare its root asset.");
        foreach (var shaderStep in prepared.BuildSteps.Steps.OfType<AssetBuildStep>().Where(step => step.AssetItem.Asset is EffectShaderAsset))
            BuildStep.LinkBuildSteps(shaderStep, rootStep);
        Directory.CreateDirectory(request.BuildDirectory); Directory.CreateDirectory(request.OutputDirectory);
        const string indexName = "index.imgui-skybox-preview";
        using var builder = new Builder(logger, request.BuildDirectory, indexName) { ThreadCount = 1 };
        builder.Root.Add(prepared.BuildSteps);
        var result = builder.Run(Builder.Mode.Build); if (result != BuildResultCode.Successful) return (int)result;
        builder.WriteIndexFile(false); builder.WriteClosureFile();
        new BundlePacker().Build(logger, session, package, indexName, request.OutputDirectory,
            builder.DisableCompressionIds, false, new List<string>());
        return logger.HasErrors ? (int)BuildResultCode.BuildError : 0;
    }
}
