using Stride.Assets.Materials;
using Stride.Assets.Models;
using Stride.Assets.Presentation.Templates;
using Stride.Assets.Textures;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static IReadOnlyDictionary<string, AssetImportOptions> RestoreModelTemplateOptions(Package package,
        (string File, AssetImportChoice[] Choices)[] files)
    {
        var initial = new Dictionary<string, AssetImportOptions>(StringComparer.OrdinalIgnoreCase);
        var settings = package.UserSettings;
        var skeletonId = settings.GetValue(ModelFromFileTemplateSettings.DefaultSkeleton);
        var skeleton = skeletonId != AssetId.Empty && package.Session.FindAsset(skeletonId)?.Asset is SkeletonAsset ? skeletonId : (AssetId?)null;
        foreach (var file in files)
        {
            var choice = file.Choices[0]; var outputs = new Dictionary<Type, bool>(choice.DefaultOutputs);
            void Restore(Type type, bool value) { if (outputs.ContainsKey(type)) outputs[type] = value; }
            Restore(typeof(MaterialAsset), settings.GetValue(ModelFromFileTemplateSettings.ImportMaterials));
            Restore(typeof(TextureAsset), settings.GetValue(ModelFromFileTemplateSettings.ImportTextures));
            Restore(typeof(AnimationAsset), settings.GetValue(ModelFromFileTemplateSettings.ImportAnimations));
            Restore(typeof(SkeletonAsset), settings.GetValue(ModelFromFileTemplateSettings.ImportSkeleton));
            if (choice.SupportsReuseSkeleton && skeleton != null) outputs[typeof(SkeletonAsset)] = false;
            initial.Add(file.File, new AssetImportOptions(choice.Id, outputs, settings.GetValue(ModelFromFileTemplateSettings.DeduplicateMaterials),
                choice.SupportsReuseSkeleton ? skeleton : null));
        }
        return initial;
    }
    private void RememberModelTemplateOptions(Package package, IReadOnlyDictionary<string, AssetImportOptions> options)
    {
        var modelOptions = options.Values.LastOrDefault(option => option.Outputs?.ContainsKey(typeof(ModelAsset)) == true);
        if (modelOptions == null) return;
        var document = GetPackageSettingsDocument(package, sessionHistory!);
        using var capture = sessionHistory!.CaptureOperationsAtomic(() =>
        {
            bool Enabled(Type type) => modelOptions.Outputs!.GetValueOrDefault(type);
            document.Set(ModelFromFileTemplateSettings.ImportMaterials, Enabled(typeof(MaterialAsset)));
            document.Set(ModelFromFileTemplateSettings.DeduplicateMaterials, modelOptions.DeduplicateMaterials);
            document.Set(ModelFromFileTemplateSettings.ImportTextures, Enabled(typeof(TextureAsset)));
            document.Set(ModelFromFileTemplateSettings.ImportAnimations, Enabled(typeof(AnimationAsset)));
            document.Set(ModelFromFileTemplateSettings.ImportSkeleton, Enabled(typeof(SkeletonAsset)));
            document.Set(ModelFromFileTemplateSettings.DefaultSkeleton, modelOptions.ReuseSkeleton ?? AssetId.Empty);
        });
        capture.Publish(sessionHistory, "Model import options");
    }
}
