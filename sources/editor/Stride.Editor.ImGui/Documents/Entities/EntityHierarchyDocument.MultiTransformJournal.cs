using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    private void CommitCanonicalMultiTransform(Entity[] entities, TransformState[] before, TransformState[] after)
    {
        if (SessionHistory == null || !ReferenceEquals(SessionHistory.Registry.Find(Asset.Id)?.Document, this)
            || entities.Any(entity => !IsSelected(entity.Id) || !CanTransformCanonicalRoot(entity)))
            throw new InvalidOperationException("The transformed hierarchy owners changed.");
        var fields = new List<ComponentField>();
        var values = new List<object?>();
        for (var index = 0; index < entities.Length; index++)
        {
            var members = GetComponentFields(entities[index].Transform);
            Add(nameof(TransformComponent.Position), before[index].Position, after[index].Position);
            Add(nameof(TransformComponent.Rotation), before[index].Rotation, after[index].Rotation);
            Add(nameof(TransformComponent.Scale), before[index].Scale, after[index].Scale);
            void Add(string name, object previous, object next)
            {
                if (Equals(previous, next)) return;
                var field = members.First(member => member.Node.Name == name);
                if (!CanEditAggregateProperty(field) || !CanEditHierarchyField(field))
                    throw new InvalidOperationException("This transform member cannot be edited.");
                fields.Add(field); values.Add(NormalizeAggregateProperty(field, next));
            }
        }
        if (fields.Count == 0) return;
        void Commit() => EditAggregatePropertyFields(fields, values);
        if (SessionHistory.IsCapturingOperations) Commit();
        else
        {
            using var capture = SessionHistory.CaptureOperationsAtomic(Commit);
            capture.Publish(SessionHistory, "Transform entities");
        }
        LastAction = entities.Length == 1 ? $"Edit transform: {entities[0].Name}" : "Transform entities";
    }
}
