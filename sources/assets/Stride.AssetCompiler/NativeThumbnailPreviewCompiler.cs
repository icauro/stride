using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Stride.Assets;
using Stride.Assets.Effect;
using Stride.Core.Assets;
using Stride.Core.Assets.Compiler;
using Stride.Core.BuildEngine;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Reflection;
using Stride.Editor.Thumbnails;
using Stride.Editor.Preview;
using Stride.Graphics;
using Stride.Shaders.Compiler;

namespace Stride.AssetCompiler;

internal static class NativeThumbnailPreviewCompiler
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int Run(string requestPath)
    {
        Stride.Assets.Thumbnails.AssetThumbnailRules.EnsureRegistered();
        using var diagnostics = EditorAssetDiagnosticLog.Create();
        var request = JsonSerializer.Deserialize<NativeThumbnailBuildParameters>(File.ReadAllText(requestPath))
            ?? throw new InvalidDataException("Invalid native thumbnail request.");
        var input = request.Inputs;
        void VerifySources()
        {
            foreach (var source in request.SourceHashes)
            {
                using var stream = File.OpenRead(source.Key);
                if (Convert.ToHexString(SHA256.HashData(stream)) != source.Value)
                    throw new InvalidDataException("A thumbnail physical source changed during its certified build: " + source.Key);
            }
        }
        VerifySources();
        if (GraphicsDevice.Platform != input.GraphicsPlatform)
            throw new InvalidOperationException("The native thumbnail worker graphics API differs from its certified request.");
        var assemblyPaths = new Dictionary<string, NativeThumbnailAssemblyInput>(StringComparer.Ordinal);
        foreach (var certificate in request.Assemblies)
        {
            var identity = AssemblyName.GetAssemblyName(certificate.Path).FullName;
            if (identity != certificate.Identity
                || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(certificate.Path))) != certificate.Sha256)
                throw new InvalidDataException("A thumbnail worker assembly differs from its certified snapshot: " + certificate.Path);
            if (!assemblyPaths.TryAdd(certificate.Identity, certificate)
                && assemblyPaths[certificate.Identity].ModuleVersionId != certificate.ModuleVersionId)
                throw new InvalidDataException("Conflicting thumbnail worker assembly modules: " + certificate.Identity);
        }
        Assembly Load(NativeThumbnailAssemblyInput certificate)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate => candidate.FullName == certificate.Identity)
                ?? Assembly.LoadFrom(certificate.Path);
            if (assembly.ManifestModule.ModuleVersionId.ToString("D") != certificate.ModuleVersionId)
                throw new InvalidDataException("The thumbnail worker module differs from the loaded editor module: " + certificate.Identity);
            return assembly;
        }
        ResolveEventHandler resolve = (_, args) => assemblyPaths.TryGetValue(args.Name, out var certificate) ? Load(certificate) : null;
        AppDomain.CurrentDomain.AssemblyResolve += resolve;
        try
        {
            foreach (var certificate in request.Assemblies)
            {
                var assembly = Load(certificate);
                RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
                AssemblyRegistry.Register(assembly, AssemblyCommonCategories.Assets);
            }
            var logger = new LoggerResult("Thumbnail"); logger.MessageLogged += (_, args) => Console.WriteLine(args.Message);
            using var session = Stride.Assets.Media.MediaPreviewPackageSession.Create(input.Assets, input.OutputDirectory, logger, assetInput =>
            {
                if (!request.AssetHashes.TryGetValue(assetInput.Id, out var expectedHash)
                    || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assetInput.Snapshot))) != expectedHash)
                    throw new InvalidDataException("A thumbnail authored snapshot differs from its certified bytes.");
            });
            var root = session.FindAsset(new AssetId(Guid.Parse(input.RootId)))
                ?? throw new InvalidDataException("The thumbnail root is absent from its authored snapshot.");
            var package = root.Package;
            using var registry = new AssetCompilerRegistry();
            var compiler = registry.GetCompiler(root.Asset.GetType(), typeof(ThumbnailCompilationContext)) as IThumbnailCompiler
                ?? throw new InvalidDataException("No actual registered native thumbnail compiler for the requested type.");
            if (compiler.GetType().AssemblyQualifiedName != request.CompilerType)
                throw new InvalidDataException("The thumbnail factory differs from the certified editor registration.");
            var settings = input.GameSettingsId == null ? GameSettingsFactory.Create()
                : session.FindAsset(new AssetId(Guid.Parse(input.GameSettingsId)))?.Asset as GameSettingsAsset
                    ?? throw new InvalidDataException("The actual thumbnail game-settings snapshot is missing.");
            var rendering = settings.GetOrCreate<RenderingSettings>();
            rendering.DefaultGraphicsProfile = input.GraphicsProfile; rendering.ColorSpace = input.ColorSpace;
            settings.GetOrCreate<Stride.Assets.Textures.TextureSettings>().TextureQuality = input.TextureQuality;
            Directory.CreateDirectory(input.BuildDirectory);
            const string index = "index.imgui-native-thumbnail";
            Builder.OpenObjectDatabase(input.BuildDirectory, index);
            using var builder = new Builder(logger, input.BuildDirectory, index) { ThreadCount = 1 };
            if (!compiler.IsStatic)
            {
                using var context = new AssetCompilerContext { Package = package, Platform = input.Platform,
                    CompilationContext = typeof(PreviewCompilationContext), BuildConfiguration = "Debug" };
                context.SetGameSettingsAsset(settings); context.OptionProperties["StrideGraphicsApi"] = input.GraphicsPlatform.ToString();
                var dependencies = new AssetDependenciesCompiler(typeof(PreviewCompilationContext));
                var prepared = dependencies.PrepareMany(context, session.Packages.SelectMany(owner => owner.Assets).Where(item => item.Asset is not GameSettingsAsset).ToList());
                foreach (var message in prepared.Messages) logger.Log(message);
                if (prepared.HasErrors || logger.HasErrors) return (int)BuildResultCode.BuildError;
                builder.Root.Add(prepared.BuildSteps);
                var dependencyResult = builder.Run(Builder.Mode.Build);
                if (dependencyResult != BuildResultCode.Successful) return (int)dependencyResult;
                // The original GPU generator mounts the common database outside a
                // build microthread. Publish only actual successful dependency outputs.
                MicrothreadLocalDatabases.AddToSharedGroup(builder.Root.OutputObjects);
                builder.Reset();
            }
            lock (MicrothreadLocalDatabases.OutOfMicrothreadDatabaseLock) MicrothreadLocalDatabases.MountCommonDatabase();
            using var effects = (EffectCompilerBase)EffectCompilerFactory.CreateEffectCompiler(MicrothreadLocalDatabases.GetSharedDatabase());
            using var generator = new ThumbnailGenerator(effects);
            var succeeded = false;
            var list = new ThumbnailListCompiler(generator, (_, completed) =>
            {
                if (completed.AssetId != root.Id || completed.Result != ThumbnailBuildResult.Succeeded || completed.ThumbnailStream == null) return;
                using var output = File.Create(request.OutputPng); completed.ThumbnailStream.CopyTo(output); succeeded = true;
            }, registry);
            // The actual runtime dependency closure was compiled above. This flag
            // avoids compiling it twice; the native thumbnail factory/commands remain unchanged.
            builder.Root.Add(list.Compile(root, settings, true));
            var result = builder.Run(Builder.Mode.Build);
            VerifySources();
            return result == BuildResultCode.Successful && succeeded && !logger.HasErrors ? 0 : (int)BuildResultCode.BuildError;
        }
        finally { AppDomain.CurrentDomain.AssemblyResolve -= resolve; }
    }
}
