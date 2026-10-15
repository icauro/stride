using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Data;

namespace Stride.Editor.ImGui;

internal sealed record GameSettingConfiguration(NodeIndex Index, ItemId? Id, Configuration Value, string Label, int? Order, IReadOnlyList<GameSettingField> Fields);
