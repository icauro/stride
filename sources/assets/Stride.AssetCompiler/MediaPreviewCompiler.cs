using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using Stride.Assets;
using Stride.Assets.Effect;
using Stride.Assets.Media;
using Stride.Assets.Models;
using Stride.Assets.Textures;
using Stride.Core.Assets;
using Stride.Core.Assets.Compiler;
using Stride.Core.Assets.Analysis;
using Stride.Core.BuildEngine;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Reflection;
using Stride.Graphics;

namespace Stride.AssetCompiler;

internal static class MediaPreviewCompiler
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Run(string requestPath)
    {
        var request = JsonSerializer.Deserialize<MediaPreviewBuildParameters>(File.ReadAllText(requestPath))
            ?? throw new ArgumentException("Invalid media preview request.");
        var logger = new LoggerResult(); logger.MessageLogged += (_, args) => Console.WriteLine(args.Message);
        foreach (var path in request.AssemblyPaths)
        {
            var identity = AssemblyName.GetAssemblyName(path);
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate => candidate.FullName == identity.FullName)
                ?? Assembly.LoadFrom(path);
            RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
            AssemblyRegistry.Register(assembly, AssemblyCommonCategories.Assets);
        }
        using var session = Stride.Assets.Media.MediaPreviewPackageSession.Create(request.Assets, request.OutputDirectory, logger);
        var root = session.FindAsset(new AssetId(Guid.Parse(request.RootId)))
            ?? throw new InvalidDataException("The actual media root is absent from the compiler request.");
        var dependencies = new AssetDependenciesCompiler(typeof(AssetCompilationContext));
        if (BuildDependencyManager.AssetCompilerRegistry.GetCompiler(root.Asset.GetType(), typeof(AssetCompilationContext)) == null)
            throw new ArgumentException("The actual preview root has no registered production asset compiler.");
        var settings = request.GameSettingsId != null
            ? session.FindAsset(new AssetId(Guid.Parse(request.GameSettingsId)))?.Asset as GameSettingsAsset
                ?? throw new InvalidDataException("The actual game-settings compiler snapshot is missing.")
            : GameSettingsFactory.Create();
        var rendering = settings.GetOrCreate<RenderingSettings>();
        rendering.DefaultGraphicsProfile = request.GraphicsProfile; rendering.ColorSpace = request.ColorSpace;
        settings.GetOrCreate<TextureSettings>().TextureQuality = request.TextureQuality;
        var package = root.Package;
        using var context = new AssetCompilerContext
        {
            Package = package, Platform = request.Platform, CompilationContext = typeof(AssetCompilationContext), BuildConfiguration = "Debug",
        };
        context.SetGameSettingsAsset(settings); context.OptionProperties["StrideGraphicsApi"] = request.GraphicsPlatform.ToString();
        // The real dependency compiler prepares imported skeletons/models/materials
        // as well as difference-animation source/reference clips and compressed sound.
        // The native dependency compiler discovers the actual root closure. The
        // captured union can also include settings/default-scene or other editor
        // assets; preparing every snapshot would make unrelated failures block it.
        var compileRoots = session.Packages.SelectMany(owner => owner.Assets)
            .Where(item => item.Id == root.Id || item.Asset is EffectShaderAsset).ToList();
        var prepared = dependencies.PrepareMany(context, compileRoots);
        foreach (var message in prepared.Messages) logger.Log(message);
        if (prepared.HasErrors || logger.HasErrors) return (int)BuildResultCode.BuildError;
        var shaders = prepared.BuildSteps.Steps.OfType<AssetBuildStep>().Where(step => step.AssetItem.Asset is EffectShaderAsset).ToArray();
        foreach (var step in prepared.BuildSteps.Steps.OfType<AssetBuildStep>().Where(step => step.AssetItem.Asset is not EffectShaderAsset))
            foreach (var shader in shaders) BuildStep.LinkBuildSteps(shader, step);
        Directory.CreateDirectory(request.BuildDirectory); Directory.CreateDirectory(request.OutputDirectory);
        const string index = "index.imgui-media-preview";
        using var builder = new Builder(logger, request.BuildDirectory, index) { ThreadCount = 1 };
        builder.Root.Add(prepared.BuildSteps); var result = builder.Run(Builder.Mode.Build);
        if (result != BuildResultCode.Successful) return (int)result;
        builder.WriteIndexFile(false); builder.WriteClosureFile();
        new BundlePacker().Build(logger, session, package, index, request.OutputDirectory,
            builder.DisableCompressionIds, false, new List<string>());
        return logger.HasErrors ? (int)BuildResultCode.BuildError : 0;
    }
}
