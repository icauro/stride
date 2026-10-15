using Stride.Assets.Presentation.SceneEditor;
using Stride.Core.Assets;
using Stride.Core.Serialization;

// Package settings are cloned through Stride's generated binary serializers.
// The settings key's closed dictionary type is a runtime root, rather than a
// member on a data contract that the serializer processor could discover.
[assembly: DataSerializerGlobal(null, typeof(Dictionary<AssetId, SceneSettingsData>))]
