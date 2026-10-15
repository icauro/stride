using System.Globalization;
using System.Runtime.CompilerServices;
using Stride.Core.Assets;
using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly ConditionalWeakTable<INodePresenter, SettingProjection> packageProjectionParents = new();
    private bool PackagePresenterReadOnly(INodePresenter? presenter)
    {
        // A projected virtual value has its own native root, but its authored
        // parent is still the original virtual presenter. Keep that ancestry
        // for control/command guards without disabling the whole subtree UI.
        while (presenter != null)
        {
            for (var current = presenter; current != null; current = current.Parent)
                if (current.IsReadOnly) return true;
            presenter = packageProjectionParents.TryGetValue(presenter.Root, out var projection) ? projection.Source : null;
        }
        return false;
    }
    private readonly PropertyStringEditor packagePropertyStrings = new();
    private bool HasPendingPackagePropertyStrings => packagePropertyStrings.HasPending;
    private void ValidatePendingPackagePropertyStrings()
    {
        packagePropertyStrings.ValidatePending();
        foreach (var pair in packageSettingTrees.ToArray())
            if (pair.Value.Pending.Count != 0 || pair.Value.Structural.Count != 0)
                CommitPackageSettingEdits(pair.Key.Item1, packageSettingAccessors[pair.Key.Item2], retainPresentation: true);
    }
    private void AbandonPendingPackagePropertyStrings() => packagePropertyStrings.AbandonPending();
    private void AbandonPendingPackagePropertyStrings(IEditorDocument owner) => packagePropertyStrings.AbandonPending(owner);
    private void ClearPackagePropertyStrings() => AbandonPendingPackagePropertyStrings();

    private sealed class PackagePropertyStringLease(PackageSettingsDocument owner, List<IDisposable> pins) : IPropertyDraftOwnerLease
    {
        public bool ContainsOwner(IEditorDocument candidate) => ReferenceEquals(owner, candidate);
        public void Dispose()
        {
            while (pins.Count != 0) { pins[^1].Dispose(); pins.RemoveAt(pins.Count - 1); }
        }
    }

    private IDisposable AcquirePackagePropertyPins(Package package, PackageSettingAccessor accessor,
        INodePresenter? presenter, Func<bool> current)
    {
        if (!current()) throw new InvalidOperationException("The package property changed before editing.");
        var history = SessionAuthoringHistory!;
        var owner = GetPackageSettingsDocument(package, history);
        var state = history.Registry.Find(owner.Path)!;
        var tree = packageSettingTrees[(package, accessor.Key)];
        var projection = tree.ActiveProjection;
        var pins = new List<IDisposable>();
        try
        {
            pins.Add(state.AcquireView());
            if (tree.KeyPresentation is { } key) pins.Add(key.AcquireTarget(key.RootPresenter));
            else if (history.Extensions?.AcquirePackagePresenters(package, accessor.Key, accessor.Key.Type) is { } providers) pins.Add(providers);
            var ancestors = new Stack<SettingProjection>();
            for (var parent = projection; parent != null; parent = parent.Parent) ancestors.Push(parent);
            foreach (var parent in ancestors) pins.Add(parent.Presentation.AcquireTarget(parent.Presentation.RootPresenter));
            if (presenter != null) pins.Add((projection?.Presentation ?? tree.KeyPresentation)!.AcquireTarget(presenter));
            return new PackagePropertyStringLease(owner, pins);
        }
        catch
        {
            var failed = new PackagePropertyStringLease(owner, pins);
            packageCommandCleanup.Add(failed.Dispose); PumpPackageCommandCleanup(); throw;
        }
    }

    private void DrawPackagePropertyString(Package package, PackageSettingAccessor accessor, string label,
        INodePresenter? presenter, bool canEdit, Func<object?> read, Action<string> commit,
        Func<object?, string>? format = null, bool commitSetting = true, Func<string, string?>? step = null,
        string? externalText = null, bool externalActive = false, bool externalCommit = false,
        Func<object?[]>? readValues = null, bool cancelOnLostFocus = false,
        PropertyDraftInput? input = null, bool reportErrors = true)
    {
        if (SessionAuthoringHistory == null) return;
        var history = SessionAuthoringHistory;
        var owner = GetPackageSettingsDocument(package, history);
        var state = history.Registry.Find(owner.Path) ?? throw new InvalidOperationException("The package settings owner is unavailable.");
        var generation = state.Generation;
        var revision = owner.AuthoringRevision;
        var tree = packageSettingTrees[(package, accessor.Key)];
        var projection = tree.ActiveProjection;
        var session = workspace?.Session;
        bool Current() => canEdit && !package.IsReadOnly && package.State >= PackageState.AssetsReady
            && !IsAuthoringPipelineUpdating && ReferenceEquals(SessionAuthoringHistory, history)
            && ReferenceEquals(workspace?.Session, session) && session?.Packages.Contains(package) == true
            && ReferenceEquals(history.Registry.Find(owner.Path), state) && state.Generation == generation
            && owner.AuthoringRevision == revision && owner.CanSave
            && package.UserSettings.Profile.CanReadRegisteredValue(accessor.Key)
            && packageSettingTrees.TryGetValue((package, accessor.Key), out var current) && ReferenceEquals(current, tree)
            && tree.Version == (history.Extensions?.Version ?? -1)
            && SameSettingValue(accessor, accessor.Get(package), tree.Source)
            && !PackagePresenterReadOnly(presenter);
        IDisposable Acquire()
        {
            return AcquirePackagePropertyPins(package, accessor, presenter, Current);
        }
        void Commit(string text)
        {
            if (!Current()) throw new InvalidOperationException("The package property changed before validation.");
            var previous = tree.ActiveProjection;
            var pendingCount = tree.Pending.Count;
            var structuralCount = tree.Structural.Count;
            tree.ActiveProjection = projection;
            try
            {
                commit(text);
                if (commitSetting) CommitPackageSettingEdits(package, accessor, retainPresentation: true);
            }
            catch
            {
                // These are the original staged callbacks, not another undo journal.
                // A failed canonical commit must not leave a duplicate for draft retry.
                if (tree.Pending.Count > pendingCount) tree.Pending.RemoveRange(pendingCount, tree.Pending.Count - pendingCount);
                if (tree.Structural.Count > structuralCount) tree.Structural.RemoveRange(structuralCount, tree.Structural.Count - structuralCount);
                throw;
            }
            finally { tree.ActiveProjection = previous; }
        }
        void Draw() => packagePropertyStrings.Draw(owner, false, Current, readValues ?? (() => [read()]), Commit, Acquire,
            step: step,
            format: format ?? (value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""),
            controlLabel: label, refreshAfterCommit: false,
            externalText: externalText, externalActive: externalActive, externalCommit: externalCommit,
            cancelOnLostFocus: cancelOnLostFocus, input: input);
        if (reportErrors) Execute(Draw); else Draw();
    }
}
