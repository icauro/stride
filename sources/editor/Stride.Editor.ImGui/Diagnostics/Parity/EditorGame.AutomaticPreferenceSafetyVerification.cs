using Stride.Core.Assets;
using Stride.Core.Settings;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static class AutomaticPreferenceSafetyKeys
    {
        static AutomaticPreferenceSafetyKeys() { }
        internal static readonly SettingsKey<int> Preference = new("Diagnostics/AutomaticPreferenceSafety/Preference", PackageUserSettings.SettingsContainer, 0);
        internal static readonly SettingsKey<int> Authored = new("Diagnostics/AutomaticPreferenceSafety/Authored", PackageUserSettings.SettingsContainer, 0);
    }

    internal object VerifyAutomaticPreferenceSafety()
    {
        if (!Environment.GetCommandLineArgs().Contains("--ui-test")) throw new InvalidOperationException("Automatic preference verification requires an isolated test host.");
        var AutomaticPreferenceProofKey = AutomaticPreferenceSafetyKeys.Preference;
        var AuthoredPreferenceProofKey = AutomaticPreferenceSafetyKeys.Authored;
        var artifacts = Environment.GetEnvironmentVariable("STRIDE_EDITOR_TEST_ARTIFACTS")
            ?? throw new InvalidOperationException("Automatic preference artifacts are not isolated.");
        var directory = System.IO.Path.Combine(artifacts, "automatic-preference-native");
        if (Directory.Exists(directory)) throw new IOException("Use a fresh automatic preference fixture.");
        Directory.CreateDirectory(directory);
        var projectPath = System.IO.Path.Combine(directory, "PreferenceOwner.csproj");
        File.WriteAllText(projectPath, "<Project><PropertyGroup><ContainsAssetTypes>false</ContainsAssetTypes></PropertyGroup></Project>");
        using var nativeSession = new PackageSession();
        var package = new Package { FullPath = System.IO.Path.Combine(directory, "PreferenceOwner.sdpkg") };
        var project = new SolutionProject(package, Guid.NewGuid(), projectPath);
        nativeSession.Projects.Add(project);
        var registry = new EditorAuthoringRegistry();
        var history = new EditorSessionHistory(registry);
        var owner = registry.GetOrCreate(package.FullPath.ToOSPath() + ".user", null, () => new PackageSettingsDocument(package, history));
        var document = owner.Document; var lease = owner.AcquireView();
        var container = PackageUserSettings.SettingsContainer;
        byte[] Profile() => container.SerializeSettingsProfile(package.UserSettings.Profile);
        byte[] Disk() => File.Exists(document.Path) ? File.ReadAllBytes(document.Path) : [];
        Guid[] Transactions() => history.Stack.RetrieveAllTransactions().Select(transaction => transaction.Id).ToArray();
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        void Deferred(int value)
        {
            var profile = Profile(); var disk = Disk(); var ids = Transactions();
            var undo = history.Stack.NextUndoTransaction; var redo = history.Stack.NextRedoTransaction;
            var revision = document.AuthoringRevision; var dirty = document.IsDirty;
            Require(!document.SaveAutomaticPreference(AutomaticPreferenceProofKey, value), "An authored settings owner did not defer automatic persistence.");
            Require(profile.AsSpan().SequenceEqual(Profile()) && disk.AsSpan().SequenceEqual(Disk()) && ids.SequenceEqual(Transactions())
                && ReferenceEquals(undo, history.Stack.NextUndoTransaction) && ReferenceEquals(redo, history.Stack.NextRedoTransaction)
                && revision == document.AuthoringRevision && dirty == document.IsDirty, "Deferred automatic preferences changed native profile, disk, history or explicit dirtiness.");
        }
        try
        {
            document.Set(AuthoredPreferenceProofKey, 1); document.Save();
            document.Set(AuthoredPreferenceProofKey, 2); history.Undo();
            Require(!document.IsDirty && history.Stack.CanRollforward, "The native fixture did not retain a clean real redo operation.");
            var redo = history.Stack.NextRedoTransaction; var ids = Transactions();
            Require(document.SaveAutomaticPreference(AutomaticPreferenceProofKey, 7), "Automatic preference native write was unexpectedly deferred.");
            Require(ids.SequenceEqual(Transactions()) && ReferenceEquals(redo, history.Stack.NextRedoTransaction) && !document.IsDirty,
                "Automatic preference persistence consumed authored redo or created a transaction.");
            var reloaded = container.LoadSettingsProfile(document.Path, false) ?? throw new InvalidOperationException("Native preference profile reload failed.");
            Require(AutomaticPreferenceProofKey.GetValue(reloaded, true) == 7 && AuthoredPreferenceProofKey.GetValue(reloaded, true) == 1,
                "Independent native profile reload lost automatic or unrelated authored values.");
            history.Redo(); Require(package.UserSettings.GetValue(AuthoredPreferenceProofKey) == 2, "Real authored redo no longer replays after automatic persistence.");
            Deferred(8); // An explicit dirty unrelated key must not be implicitly saved.
            document.Save(); history.Stack.Clear();
            document.Set(AutomaticPreferenceProofKey, 11); document.Save(); Deferred(12);
            history.Undo(); Deferred(13); history.Redo(); Deferred(14);
            document.Save(); history.Stack.Clear();
            using (var captured = history.CaptureOperationsAtomic(() => document.Set(AutomaticPreferenceProofKey, 17)))
                captured.Publish(history, "Native compound preference proof", operation => new GuardedOperation(operation, (_, apply) => apply()));
            document.Save(); Deferred(18);
            history.Undo(); Deferred(19); history.Redo(); Deferred(20);
            document.Save(); history.Stack.Clear();

            var profileBeforeConflict = Profile(); var revisionBeforeConflict = document.AuthoringRevision;
            var loadedHashBeforeConflict = package.UserSettings.Profile.LoadedContentHash.ToArray();
            var foreignBytes = System.Text.Encoding.UTF8.GetBytes("# independently changed private settings file\n");
            File.WriteAllBytes(document.Path, foreignBytes);
            var rejected = false;
            try { document.SaveAutomaticPreference(AutomaticPreferenceProofKey, 23); }
            catch (IOException) { rejected = true; }
            Require(rejected && profileBeforeConflict.AsSpan().SequenceEqual(Profile()) && foreignBytes.AsSpan().SequenceEqual(Disk())
                && revisionBeforeConflict == document.AuthoringRevision && !document.IsDirty
                && loadedHashBeforeConflict.AsSpan().SequenceEqual(package.UserSettings.Profile.LoadedContentHash.Span)
                && history.Stack.IsEmpty, "External conflict overwrote the file or failed exact native entry/hash restoration.");

            registry.Unpublish(owner); // Real canonical transition while its actual view lease remains alive.
            rejected = false;
            try { document.SaveAutomaticPreference(AutomaticPreferenceProofKey, 29); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected && !owner.IsPublished && !owner.IsDisposed && profileBeforeConflict.AsSpan().SequenceEqual(Profile())
                && foreignBytes.AsSpan().SequenceEqual(Disk()) && history.Stack.IsEmpty, "A retained historical preference owner wrote or mutated state.");
            return new { schema = 1, nativeDurableReload = true, authoredRedoPreserved = true, explicitDirtyDeferred = true,
                savedSameKeyUndoRedoDeferred = true, wrappedCompositeUndoRedoDeferred = true, externalConflictRestored = true,
                historicalOwnerRejected = true, coverage = "Actual native private package/profile, canonical registry and authored transaction stack; service boundary, no camera widget or OS input claim." };
        }
        finally
        {
            try { history.Dispose(); }
            finally { try { lease.Dispose(); } finally { registry.Dispose(); } }
        }
    }
}
