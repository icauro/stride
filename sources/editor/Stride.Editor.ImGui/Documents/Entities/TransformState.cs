using System.Security.Cryptography;
using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Core.Assets.Visitors;
using Stride.Core.Mathematics;
using Stride.Core.Reflection;
using Stride.Core.Transactions;
using Stride.Core.Yaml;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal readonly record struct TransformState(Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    public static TransformState Read(Entity entity) => new(entity.Transform.Position, entity.Transform.Rotation, entity.Transform.Scale);
    public void Apply(Entity entity)
    {
        entity.Transform.Position = Position;
        entity.Transform.Rotation = Rotation;
        entity.Transform.Scale = Scale;
    }
}
