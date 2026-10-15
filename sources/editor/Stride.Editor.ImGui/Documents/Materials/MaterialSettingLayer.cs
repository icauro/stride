using Stride.Core.Assets;
using Stride.Core.Reflection;
using Stride.Rendering.Materials;

namespace Stride.Editor.ImGui;

internal sealed record MaterialSettingLayer(ItemId Id, MaterialBlendLayer Value, IReadOnlyList<MaterialSettingField> Fields);
