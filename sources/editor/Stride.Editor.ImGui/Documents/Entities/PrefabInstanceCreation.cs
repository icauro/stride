using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed record PrefabInstanceCreation(Guid InstanceId, IReadOnlyList<Entity> Roots, IReadOnlyDictionary<Guid, Guid> IdentityRemapping);
