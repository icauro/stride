using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawRetainedModelNodeName(string identity, IReadOnlyList<ComponentField> fields,
        IReadOnlyList<EntityHierarchyDocument> fieldOwners, bool mixed, Func<bool> canEdit,
        Func<IReadOnlyList<ModelNodeNameChoice>> choices, Action<string> apply)
    {
        var first = fields[0];
        var owners = fieldOwners.Distinct().ToArray();
        var feedback = Gui.GetID("##NodeName");
        bool Editable() => !IsAuthoringPipelineUpdating && canEdit()
            && fields.Select((field, index) => field.SupportsEditing && field.Component.Entity is { } entity
                && fieldOwners[index].IsSelected(entity.Id)
                && sessionHistory?.Registry.Find(fieldOwners[index].Asset.Id) is { IsPublished: true, IsDisposed: false } state
                && ReferenceEquals(state.Document, fieldOwners[index])).All(current => current);
        object?[] Read() => fields.SelectMany((field, index) => new object?[]
        {
            field.Node.Retrieve(), field.Node, field.Component, fieldOwners[index].Path, fieldOwners[index].EditRevision,
            sessionHistory?.Registry.Find(fieldOwners[index].Asset.Id)?.Generation,
            sessionHistory?.Extensions?.Version,
        }).Append(string.Join("\n", choices().Select(choice => choice.Depth + ":" + choice.Name))).ToArray();
        IDisposable Acquire()
        {
            var leases = new List<IDisposable>();
            var cleanup = new PropertyStringOwners(leases, owners);
            try
            {
                if (!Editable()) throw new InvalidOperationException("The selected node-name property is no longer current.");
                leases.Add(AcquirePropertyStringOwners(owners));
                foreach (var field in fields)
                    if (field.Presentation?.AcquireCommandTarget(field) is { } target) leases.Add(target);
                return cleanup;
            }
            catch (Exception failure)
            {
                try { cleanup.Dispose(); }
                catch (Exception release)
                {
                    packageCommandCleanup.Add(cleanup.Dispose);
                    throw new AggregateException(failure, release);
                }
                throw;
            }
        }
        try
        {
            PropertyGrid.ModelNodeNames.Draw(identity, first.Node.Retrieve() as string, mixed, Editable(), choices,
                (input, cancel) => PropertyGrid.Strings.Draw(first.Node, mixed, Editable, Read, apply, Acquire,
                    controlLabel: "##NodeName", input: input, deferDeactivation: true, cancelInput: cancel),
                value =>
                {
                    if (!choices().Any(choice => choice.Name == value))
                        throw new InvalidOperationException("The selected model node is no longer available.");
                    PropertyGrid.Strings.ValidateExternal(first.Node, feedback, value, Editable, Read, apply, Acquire);
                }, cancel =>
                {
                    if (cancel) PropertyGrid.Strings.Abandon(first.Node, feedback);
                    else PropertyGrid.Strings.ValidatePendingForIdentity(first.Node);
                }, (id, text, selected) => DrawNativeLiteralSelectable(id, text, selected));
        }
        catch (Exception error)
        {
            PropertyConversionFeedback.Fail(feedback, error); statusMessage = error.Message;
        }
        PropertyConversionFeedback.Draw(feedback);
    }
}
