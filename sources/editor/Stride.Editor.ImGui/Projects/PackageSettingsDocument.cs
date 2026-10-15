using Stride.Core.Assets;
using Stride.Core.Settings;
using Stride.Core.Transactions;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

/// <summary>Authored package user settings on the same session stack as asset edits.</summary>
internal sealed partial class PackageSettingsDocument : IEditorDocument, ISessionAuthoringDocument
{
    private readonly Package package;
    private readonly EditorDocumentHistoryScope history;
    private long revision, nextRevision, savedRevision;
    private long authoringStamp;
    private byte[]? savedDiskHash;
    private Action? automaticPreferenceCompensation;
    private Action? automaticPreferenceAcknowledgment;
    public PackageSettingsDocument(Package package, EditorSessionHistory session)
    {
        this.package = package; history = new(this, session, "Edit package user settings");
        var loadedHash = package.UserSettings.Profile.LoadedContentHash;
        if (!loadedHash.IsEmpty) savedDiskHash = loadedHash.ToArray();
        else if (File.Exists(Path)) throw new IOException("The package user settings changed on disk. Reload them before editing.");
    }
    public string Path => package.FullPath.ToOSPath() + ".user";
    public bool IsDirty => revision != savedRevision;
    public long AuthoringRevision => authoringStamp;
    public bool CanSave => !package.IsReadOnly;
    public bool CanUndo => history.Stack.CanRollback;
    public bool CanRedo => history.Stack.CanRollforward;
    public EditorSessionHistory? SessionHistory => history.SessionHistory;
    public void BindAuthoringState(EditorAuthoringState state) => history.Bind(state);
    public void CommitEdits() { }
    public void Undo() => SessionHistory!.Undo();
    public void Redo() => SessionHistory!.Redo();
    public void Dispose() { RetryAutomaticPreferenceCompensation(); history.EnsureMayDispose(); }
    private interface ISettingsHistoryOwnership
    {
        bool Owns(Package target, SettingsKey key);
    }
    private static IEnumerable<Operation> SettingsOperations(IReadOnlyTransaction transaction)
    {
        foreach (var operation in transaction.Operations)
        {
            yield return operation;
            if (operation is IReadOnlyTransaction nested)
                foreach (var child in SettingsOperations(nested)) yield return child;
        }
    }
    private void RetryAutomaticPreferenceCompensation()
    {
        if (automaticPreferenceAcknowledgment != null)
        {
            automaticPreferenceAcknowledgment();
            automaticPreferenceAcknowledgment = null;
        }
        if (automaticPreferenceCompensation == null) return;
        automaticPreferenceCompensation();
        automaticPreferenceCompensation = null;
    }
    /// <summary>Persists automatic viewport preferences without creating authored undo operations.</summary>
    internal bool SaveAutomaticPreference<T>(SettingsKey<T> key, T value)
    {
        RetryAutomaticPreferenceCompensation();
        history.EnsureMaySave();
        if (!CanSave || !ReferenceEquals(key.Container, PackageUserSettings.SettingsContainer)
            || !package.UserSettings.Profile.CanReadRegisteredValue(key))
            throw new InvalidOperationException("This registered package preference cannot be persisted.");
        // Explicit dirty edits and retained same-key (including redo) operations
        // own their exact entry fingerprints. Automatic persistence defers rather
        // than saving those edits or invalidating their replay snapshots.
        if (IsDirty || history.Stack.RetrieveAllTransactions().SelectMany(SettingsOperations)
            .OfType<ISettingsHistoryOwnership>().Any(operation => operation.Owns(package, key))) return false;
        var profile = package.UserSettings.Profile;
        var profileHash = profile.LoadedContentHash;
        var expectedHash = !profileHash.IsEmpty ? profileHash.ToArray() : savedDiskHash;
        var before = profile.CaptureAuthoredEntry(key);
        var after = Snapshot(value);
        if (SerializeValue(package.UserSettings.GetValue(key)).AsSpan().SequenceEqual(SerializeValue(after))) return true;
        using var saving = PackageUserSettings.SettingsContainer.BeginGuardedProfileSave(profile);
        automaticPreferenceCompensation = () => profile.RestoreAuthoredEntry(key, before);
        OwnedFileWriteResult result = default;
        Action acknowledge = () =>
        {
            if (!System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path)).AsSpan().SequenceEqual(result.ContentHash)
                || !System.Security.Cryptography.SHA256.HashData(PackageUserSettings.SettingsContainer.SerializeSettingsProfile(profile))
                    .AsSpan().SequenceEqual(result.ContentHash))
                throw new IOException("The automatic preference publication changed before its native acknowledgment; its exact receipt remains owned.");
            PackageUserSettings.SettingsContainer.AcknowledgeSavedSettingsProfile(profile, Path, result.ContentHash);
            savedDiskHash = result.ContentHash;
            savedFileGeneration++; authoringStamp++;
        };
        try
        {
            profile.ApplyAuthoredValue(key, after);
            var bytes = PackageUserSettings.SettingsContainer.SerializeSettingsProfile(profile);
            result = WindowsOwnedAssetFileWriter.Write(Path, expectedHash, bytes);
        }
        catch (Exception failure)
        {
            try { RetryAutomaticPreferenceCompensation(); }
            catch (Exception compensation)
            {
                throw new AggregateException("Automatic preference write and exact native entry restoration failed; the owner retains compensation.", failure, compensation);
            }
            throw;
        }
        // The actual durable file receipt is acknowledged without allocating an
        // authored revision, consuming redo or acknowledging explicit edits.
        automaticPreferenceCompensation = null;
        automaticPreferenceAcknowledgment = acknowledge;
        RetryAutomaticPreferenceCompensation();
        return true;
    }
    public void Set<T>(SettingsKey<T> key, T value)
    {
        RetryAutomaticPreferenceCompensation();
        if (!CanSave) throw new InvalidOperationException("This package can't be edited.");
        if (!package.UserSettings.Profile.CanReadRegisteredValue(key)) throw new InvalidOperationException("The registered setting is inactive or its original YAML cannot be represented by this key generation.");
        var profileHash = package.UserSettings.Profile.LoadedContentHash;
        if (!profileHash.IsEmpty && (savedDiskHash == null || !profileHash.Span.SequenceEqual(savedDiskHash)))
        {
            if (IsDirty) throw new IOException("The package user settings changed while they were being edited.");
            savedDiskHash = profileHash.ToArray();
        }
        var before = Snapshot(package.UserSettings.GetValue(key));
        var after = Snapshot(value);
        var beforeBytes = SerializeValue(before); var afterBytes = SerializeValue(after);
        if (beforeBytes.AsSpan().SequenceEqual(afterBytes)) return;
        var beforeEntry = package.UserSettings.Profile.CaptureAuthoredEntry(key);
        var beforeRevision = revision; var afterRevision = ++nextRevision;
        void Restore(SettingsEntrySnapshot next, long nextRevisionValue) { package.UserSettings.Profile.RestoreAuthoredEntry(key, next); revision = nextRevisionValue; authoringStamp++; }
        try
        {
            package.UserSettings.Profile.ApplyAuthoredValue(key, after); revision = afterRevision; authoringStamp++;
            var afterEntry = package.UserSettings.Profile.CaptureAuthoredEntry(key);
            using var transaction = history.CreateTransaction("Edit " + key.DisplayName);
            history.Record(new SettingOperation<T>(package, key, beforeEntry, afterEntry,
                () => Restore(beforeEntry, beforeRevision), () => Restore(afterEntry, afterRevision)));
        }
        catch { Restore(beforeEntry, beforeRevision); throw; }
    }
    public void EditValue<T>(SettingsKey<T> key, Action<IObjectNode> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        EditValueReplacingRoot(key, root => { edit(root); return root.Retrieve(); });
    }
    public void EditValueReplacingRoot<T>(SettingsKey<T> key, Func<IObjectNode, object?> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (!CanSave || !ReferenceEquals(key.Container, PackageUserSettings.SettingsContainer)
            || !package.UserSettings.Profile.CanReadRegisteredValue(key)) throw new InvalidOperationException("This registered package setting cannot be edited.");
        var value = Snapshot(package.UserSettings.GetValue(key));
        if (value == null) throw new InvalidOperationException("Create the registered setting value before editing its members.");
        var nodes = new NodeContainer();
        var root = nodes.GetOrCreateNode(value) ?? throw new InvalidOperationException("The setting value has no Quantum root.");
        Set(key, (T)edit(root)!);
    }
    private static T Snapshot<T>(T value) => (T)AssetCloner.Clone(value)!;
    private static byte[] SerializeValue<T>(T value)
    {
        using var stream = new MemoryStream();
        SettingsYamlSerializer.Default.Serialize(stream, value);
        return stream.ToArray();
    }
    public void Save()
    {
        RetryAutomaticPreferenceCompensation();
        history.EnsureMaySave();
        if (!CanSave) throw new InvalidOperationException("This package can't be edited.");
        if (!IsDirty) return;
        using var saving = PackageUserSettings.SettingsContainer.BeginGuardedProfileSave(package.UserSettings.Profile);
        var bytes = PackageUserSettings.SettingsContainer.SerializeSettingsProfile(package.UserSettings.Profile);
        var result = WindowsOwnedAssetFileWriter.Write(Path, savedDiskHash, bytes);
        savedDiskHash = result.ContentHash;
        PackageUserSettings.SettingsContainer.AcknowledgeSavedSettingsProfile(package.UserSettings.Profile, Path, savedDiskHash);
        savedRevision = revision;
        history.Saved(savedDiskHash);
        savedFileGeneration++;
    }
    private sealed class SettingOperation<T>(Package package, SettingsKey<T> key, SettingsEntrySnapshot before, SettingsEntrySnapshot after, Action restore, Action apply) : Operation, ISettingsHistoryOwnership
    {
        public bool Owns(Package target, SettingsKey candidate) => ReferenceEquals(package, target) && key.Name.Equals(candidate.Name);
        protected override void Rollback()
        {
            if (package.UserSettings.Profile.CaptureAuthoredEntry(key).ContentFingerprint != after.ContentFingerprint) throw new InvalidOperationException("The package setting changed.");
            try { restore(); } catch { apply(); throw; }
        }
        protected override void Rollforward()
        {
            if (package.UserSettings.Profile.CaptureAuthoredEntry(key).ContentFingerprint != before.ContentFingerprint) throw new InvalidOperationException("The package setting changed.");
            try { apply(); } catch { restore(); throw; }
        }
    }
}
