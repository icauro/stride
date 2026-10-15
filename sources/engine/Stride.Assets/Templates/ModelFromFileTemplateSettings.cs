// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using Stride.Core.Assets;
using Stride.Core.Settings;

namespace Stride.Assets.Presentation.Templates
{
    /// <summary>The original shared package-scoped model template preferences.</summary>
    public static class ModelFromFileTemplateSettings
    {
        public static SettingsKey<bool> ImportMaterials = new SettingsKey<bool>("Templates/ModelFromFile/ImportMaterials", PackageUserSettings.SettingsContainer, true);
        public static SettingsKey<bool> DeduplicateMaterials = new SettingsKey<bool>("Templates/ModelFromFile/DeduplicateMaterials", PackageUserSettings.SettingsContainer, true);
        public static SettingsKey<bool> ImportTextures = new SettingsKey<bool>("Templates/ModelFromFile/ImportTextures", PackageUserSettings.SettingsContainer, true);
        public static SettingsKey<bool> ImportAnimations = new SettingsKey<bool>("Templates/ModelFromFile/ImportAnimations", PackageUserSettings.SettingsContainer, true);
        public static SettingsKey<bool> ImportSkeleton = new SettingsKey<bool>("Templates/ModelFromFile/ImportSkeleton", PackageUserSettings.SettingsContainer, true);
        public static SettingsKey<AssetId> DefaultSkeleton = new SettingsKey<AssetId>("Templates/ModelFromFile/DefaultSkeleton", PackageUserSettings.SettingsContainer, AssetId.Empty);
    }
}
