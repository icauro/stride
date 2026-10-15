using Stride.GameStudio.Helpers;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal object VerifyRecentProjectsParity(string ownedDirectory)
    {
        var artifactRoot = Environment.GetEnvironmentVariable("STRIDE_EDITOR_TEST_ARTIFACTS");
        var privateRoot = EditorUserDataPaths.OverrideRoot;
        static bool Within(string path, string root)
            => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!args.Contains("--ui-test") || artifactRoot == null || privateRoot == null
            || !Within(privateRoot, artifactRoot) || !Within(ownedDirectory, artifactRoot))
            throw new InvalidOperationException("MRU verification requires the actual runner's isolated settings and artifact directories.");
        Directory.CreateDirectory(ownedDirectory);
        var nativePath = Path.Combine(privateRoot, "GameStudioInternal.conf");
        var originalBytes = File.Exists(nativePath) ? File.ReadAllBytes(nativePath) : null;
        var previousPreferences = preferences; var previousNative = nativeRecentProjects;
        var previousMetadata = presentedRecentMetadata; var previousPaths = presentedRecentProjects;
        var previousObserved = observedNativeRecentProjects; var previousCompatible = observedCompatibleRecentProjects;
        InternalSessionsSettings? fixtureNative = null;
        Exception? primary = null;
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        try
        {
            if (File.Exists(nativePath)) File.Delete(nativePath);
            fixtureNative = new InternalSessionsSettings();
            preferences = new EditorPreferencesStore(Path.Combine(ownedDirectory, "preferences.json"));
            nativeRecentProjects = fixtureNative;
            observedNativeRecentProjects = null; observedCompatibleRecentProjects = [];
            var currentVersion = StrideGameStudio.EditorVersionMajor;
            const string olderVersion = "1.3", futureVersion = "999.0";
            Require(string.Compare(olderVersion, currentVersion, StringComparison.Ordinal) < 0
                && string.Compare(futureVersion, currentVersion, StringComparison.Ordinal) > 0,
                "The genuine native version-filter fixture must straddle the deployed editor version.");
            var current = Enumerable.Range(0, 22).Select(index => Path.Combine(ownedDirectory, "current-" + index + ".sdpkg")).ToArray();
            var older = Enumerable.Range(0, 3).Select(index => Path.Combine(ownedDirectory, "older-" + index + ".sdpkg")).ToArray();
            var future = Enumerable.Range(0, 3).Select(index => Path.Combine(ownedDirectory, "future-" + index + ".sdpkg")).ToArray();
            foreach (var path in current) fixtureNative.Add(path, currentVersion);
            foreach (var path in older) fixtureNative.Add(path, olderVersion);
            foreach (var path in future) fixtureNative.Add(path, futureVersion);
            using (var reopened = new InternalSessionsSettings())
            {
                reopened.Refresh(true);
                Require(reopened.Error == null && reopened.Entries.Count(item => item.Version == currentVersion) == 20
                    && reopened.Entries.Count(item => item.Version == olderVersion) == 3
                    && reopened.Entries.Count(item => item.Version == futureVersion) == 3
                    && !reopened.Entries.Any(item => current.Take(2).Contains(item.Path, StringComparer.OrdinalIgnoreCase))
                    && current.Skip(2).All(path => reopened.Entries.Any(item => item.Version == currentVersion
                        && string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))),
                    "Independent native storage must retain twenty current-version entries and other version buckets.");
                Require(reopened.Entries.Zip(reopened.Entries.Skip(1), (left, right) => left.Timestamp >= right.Timestamp).All(value => value),
                    "Native persisted MRU entries must retain descending timestamp order.");
            }
            var legacy = Path.Combine(ownedDirectory, "legacy-unknown.sdpkg");
            Require(preferences.TryUpdate(value => value.RecentProjects = future.Concat(new[] { legacy }).ToList()),
                "The real compatibility preferences must durably publish the fixture paths.");
            fixtureNative.Refresh(true);
            var presented = PresentedRecentProjectMetadata().ToArray();
            var expected = fixtureNative.Entries.Where(item => item.Version != futureVersion).Take(10).ToArray();
            Require(presented.Length == 10 && presented.SequenceEqual(expected)
                && !presented.Any(item => future.Contains(item.Path, StringComparer.OrdinalIgnoreCase)),
                "Actual shared presentation must take ten eligible native entries and exclude newer legacy duplicates.");
            Require(ReferenceEquals(PresentedRecentProjectMetadata(), PresentedRecentProjectMetadata()),
                "An unchanged native/compatibility snapshot must reuse its actual presentation cache.");
            var removed = presented[0].Path;
            var beforeRemoval = fixtureNative.Entries.ToArray();
            Action staleRemove = () => RemoveRecentProject(removed);
            RemoveRecentProject(removed);
            using (var reopened = new InternalSessionsSettings())
            {
                reopened.Refresh(true);
                var beforeOthers = beforeRemoval.Where(item => !string.Equals(item.Path, removed, StringComparison.OrdinalIgnoreCase)).ToArray();
                Require(reopened.Error == null && !reopened.Entries.Any(item => string.Equals(item.Path, removed, StringComparison.OrdinalIgnoreCase))
                    && reopened.Entries.SequenceEqual(beforeOthers),
                    "Actual remove must durably remove its exact native member while preserving all other entries.");
            }
            var afterRemove = File.ReadAllBytes(nativePath);
            var staleRejected = false;
            try { staleRemove(); } catch (InvalidOperationException) { staleRejected = true; }
            Require(staleRejected && File.ReadAllBytes(nativePath).AsSpan().SequenceEqual(afterRemove),
                "The already captured remove callback must reject lost persisted membership without changing native bytes.");
            fixtureNative.Clear(new[] { currentVersion, olderVersion });
            fixtureNative.Refresh(true);
            var fallback = PresentedRecentProjectMetadata().ToArray();
            Require(fallback.Length == 1 && fallback[0].Path == legacy && fallback[0].Version == ""
                && future.All(path => ResolveRecentProjectPath(path) == null) && ResolveRecentProjectPath(legacy) == legacy,
                "Only the genuinely unknown legacy path may fall back; known newer native paths remain excluded.");
            var legacyReopened = new EditorPreferencesStore(Path.Combine(ownedDirectory, "preferences.json")); legacyReopened.Load();
            Require(!legacyReopened.IsBlocked && legacyReopened.Current.RecentProjects.SequenceEqual(future.Concat(new[] { legacy })),
                "Native MRU removal must preserve unrelated durable compatibility paths.");
            return new { currentVersion, retainedPerVersion = 20, visibleEligibleLimit = 10,
                exactNativeRemove = true, staleCallbackRefused = true, unknownLegacyVersionPreserved = true,
                knownNewerLegacyExcluded = true, independentNativeReload = true,
                scope = "Private native MRU storage and shared presentation/Remove commands; no Explorer, clipboard, project opening or startup/menu gestures." };
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            var cleanup = new List<Exception>();
            try { fixtureNative?.Dispose(); } catch (Exception error) { cleanup.Add(error); }
            preferences = previousPreferences; nativeRecentProjects = previousNative;
            presentedRecentMetadata = previousMetadata; presentedRecentProjects = previousPaths;
            observedNativeRecentProjects = previousObserved; observedCompatibleRecentProjects = previousCompatible;
            try
            {
                if (originalBytes != null) File.WriteAllBytes(nativePath, originalBytes);
                else if (File.Exists(nativePath)) File.Delete(nativePath);
                if (originalBytes == null ? File.Exists(nativePath) : !File.ReadAllBytes(nativePath).AsSpan().SequenceEqual(originalBytes))
                    throw new IOException("The private native MRU baseline was not restored exactly.");
                previousNative?.Refresh(true);
            }
            catch (Exception error) { cleanup.Add(error); }
            if (cleanup.Count != 0) throw new AggregateException("Private MRU verification restoration failed.",
                primary == null ? cleanup : new[] { primary }.Concat(cleanup));
        }
    }
}
