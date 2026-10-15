using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawNativeCompositorDictionaryAdd(AssetMetadataDocument source, IReadOnlyList<CompositorProperty> properties)
    {
        var target = CaptureCompositorDraftTarget(source, properties, canEditField: source.CanAddNativeCompositorDictionaryKey);
        if (target.Fields[0].Collection?.Descriptor is not DictionaryDescriptor descriptor) return;
        object?[] Certificates()
        {
            var result = target.Read().ToList();
            result.Add(source.EditRevision); result.Add(source.SessionHistory?.Extensions?.Version);
            foreach (var field in target.Fields)
                foreach (var index in field.Collection!.Indices ?? [])
                {
                    result.Add(field.Collection.IndexToId(index)); result.Add(index); result.Add(field.Collection.Retrieve(index));
                }
            return result.ToArray();
        }
        void Add(object? parameter)
        {
            ValidatePendingPropertyStrings(); source.CommitEdits();
            if (!target.Editable()) throw new InvalidOperationException("The native compositor dictionary owner changed during validation.");
            using var lease = new RetainedPathLease(this, target.Acquire());
            UseTextureSettingsCommandContext();
            var fields = target.Fields.DistinctBy(field => field.Collection).ToArray();
            var owners = new List<EditorPresenterTarget>(); EditorPresenterTarget? combined = null; var transferred = false;
            try
            {
                foreach (var field in fields)
                    if (source.AcquireCompositorFactoryTarget(field) is { } owner) owners.Add(owner);
                if (owners.Count == fields.Length)
                {
                    combined = new(owners);
                    var command = NodePresenterCombination.Commands(combined.Presenters).FirstOrDefault(command => command.Name == "AddPrimitiveKey");
                    if (command != null)
                    {
                        if (combined.Presenters.Any(presenter => presenter.IsReadOnly)) throw new InvalidOperationException("The native dictionary is read-only.");
                        StartEditorExtensionCommand(source, combined, command, parameter); transferred = true; return;
                    }
                    if (combined.Presenters.Any(presenter => presenter.Commands.Any(candidate => candidate.Name == "AddPrimitiveKey")))
                        throw new InvalidOperationException("The registered dictionary command does not support this native combination.");
                }
                else if (owners.SelectMany(owner => owner.Presenters).Any(presenter => presenter.Commands.Any(command => command.Name == "AddPrimitiveKey")))
                    throw new InvalidOperationException("A selected native dictionary presenter is no longer available.");
                source.AddNativeCompositorDictionaryKeys(target.Fields, parameter);
            }
            finally
            {
                if (!transferred)
                {
                    if (combined != null) Release(combined);
                    else foreach (var owner in owners) Release(owner);
                }
            }
        }
        void Release(EditorPresenterTarget owner)
        {
            try { owner.Dispose(); } catch { packageCommandCleanup.Add(owner.Dispose); throw; }
        }
        DrawNativeDictionaryAdd(target.Fields[0].Collection!, descriptor, target.Editable, Certificates, Add, target.Acquire);
    }
}
