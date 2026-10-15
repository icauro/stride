using Stride.Core.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool TryDrawCertifiedPropertyEditor(AssetMetadataDocument owner, IMemberNode member, string label,
        Func<bool> current, Func<bool> canEdit, Func<object?> value, Func<object?[]> read,
        Func<object?[]> readTarget, Action<object?> stage, Action finish)
    {
        var extensions = owner.SessionHistory?.Extensions;
        if (extensions?.HasPrimitiveEditor(member.Type) != true) return false;
        if (!BeginInspectorPropertyRow(label)) return true;
        try
        {
            var leases = new List<IDisposable>();
            PropertyStringOwners ownership;
            try
            {
                if (!current()) throw new InvalidOperationException("The property editor owner changed.");
                leases.Add(AcquirePropertyStringOwner(owner));
                leases.Add(owner.AcquireExtensionCommandTarget(member)
                    ?? throw new InvalidOperationException("The property editor has no native presenter target."));
                ownership = new(leases, [owner]);
            }
            catch (Exception failure)
            {
                var cleanup = new PropertyStringOwners(leases, [owner]);
                try { cleanup.Dispose(); }
                catch (Exception release) { packageCommandCleanup.Add(cleanup.Dispose); throw new AggregateException(failure, release); }
                throw;
            }
            using var held = new RetainedPathLease(this, ownership);
            var expected = read(); var rendering = true;
            bool Current() => rendering && !IsAuthoringPipelineUpdating && current() && expected.SequenceEqual(read());
            void Validate()
            {
                if (!Current()) throw new InvalidOperationException("The property editor context changed.");
                var target = readTarget();
                ValidatePendingPropertyStrings();
                if (!rendering || IsAuthoringPipelineUpdating || !current() || !target.SequenceEqual(readTarget()))
                    throw new InvalidOperationException("The property changed during pending validation.");
                expected = read();
            }
            try
            {
                extensions.TryDrawPrimitive(new(member.Type, value(), label, Current() && canEdit(), replacement =>
                {
                    if (!Current() || !canEdit()) throw new InvalidOperationException("The property is read-only or changed.");
                    if (ContentAssetReference.AreEqual(value(), replacement)) return;
                    Validate();
                    if (!canEdit()) throw new InvalidOperationException("The property became read-only.");
                    UseTextureSettingsCommandContext(); stage(replacement); expected = read();
                }, () =>
                {
                    Validate(); UseTextureSettingsCommandContext(); finish(); expected = read();
                }, (name, parameter) =>
                {
                    if (!Current()) throw new InvalidOperationException("The property command context changed.");
                    var target = owner.AcquireExtensionCommandTarget(member)
                        ?? throw new InvalidOperationException("The property command target changed.");
                    var transferred = false;
                    try
                    {
                        var command = NodePresenterCombination.Commands(target.Presenters)
                            .SingleOrDefault(candidate => candidate.Name == name && candidate.CanExecute(target.Presenters, parameter))
                            ?? throw new InvalidOperationException("The native property command is not available.");
                        Validate(); finish(); expected = read();
                        if (!current() || !NodePresenterCombination.Commands(target.Presenters).Contains(command)
                            || !command.CanExecute(target.Presenters, parameter)) throw new InvalidOperationException("The native property command changed.");
                        transferred = true; StartEditorExtensionCommand(owner, target, command, parameter);
                    }
                    finally { if (!transferred) ReleaseHierarchyFactoryTarget(target); }
                }));
            }
            finally { rendering = false; }
        }
        finally { Gui.EndTable(); }
        return true;
    }
    private object?[] CertifiedPropertyTarget(AssetMetadataDocument owner, IMemberNode member)
        => [member, member.Parent.Retrieve(), member.Retrieve(), member.Type,
            member.Retrieve() is DateTime date ? date.Kind : null, owner.Path,
            sessionHistory?.Registry.Find(owner.AuthoredAsset.Id)?.Generation, owner.SessionHistory?.Extensions?.Version];
    private bool IsCertifiedPropertyOwner(AssetMetadataDocument owner)
        => IsCurrentMetadataControlOwner(owner) && sessionHistory?.Registry.Find(owner.AuthoredAsset.Id) is { IsPublished: true, IsDisposed: false } state
            && ReferenceEquals(state.Document, owner);
}
