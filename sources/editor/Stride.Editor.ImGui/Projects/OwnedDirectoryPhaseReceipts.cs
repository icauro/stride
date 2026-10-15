namespace Stride.Editor.ImGui;

internal sealed class OwnedDirectoryPhaseReceipts(string root)
{
    private readonly string canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    private sealed class DirectoryReceipt(string path, OwnedDirectoryIdentity identity)
    {
        internal readonly string Path = path;
        internal OwnedDirectoryIdentity Identity = identity;
        internal bool Present = true;
    }
    private readonly List<DirectoryReceipt> created = [];
    private readonly Dictionary<string, OwnedDirectoryIdentity> existingParents = new(StringComparer.OrdinalIgnoreCase);
    internal bool HasDirectories => created.Count != 0;
    internal bool HasPresentDirectories => created.Any(directory => directory.Present);
    internal bool HasAbsentDirectories => created.Any(directory => !directory.Present);
    internal bool[] CaptureDirectoryPhases() => created.Select(directory => directory.Present).ToArray();
    internal void RestoreDirectoryPhases(bool[] presence)
    {
        if (presence.Length != created.Count) throw new InvalidOperationException("The owned directory receipt set changed.");
        ValidateOwnedDirectories();
        // Restore only this attempt's prior phases, not directories already absent
        // before another volume failed. Successful compensation remains receipted.
        for (var index = created.Count - 1; index >= 0; index--)
            if (!presence[index] && created[index].Present) RemoveOne(created[index]);
        for (var index = 0; index < created.Count; index++)
            if (presence[index] && !created[index].Present) RestoreOne(created[index]);
    }

    private bool IsRoot(string directory) => string.Equals(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, canonicalRoot, StringComparison.OrdinalIgnoreCase);
    private void ValidatePath(string directory)
    {
        if (!directory.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase) && !IsRoot(directory))
            throw new InvalidOperationException("The source directory is outside its project.");
    }
    private void ValidateParentChain(string directory)
    {
        var parent = Path.GetDirectoryName(directory);
        while (parent != null)
        {
            ValidatePath(parent);
            var owned = created.FirstOrDefault(receipt => string.Equals(receipt.Path, parent, StringComparison.OrdinalIgnoreCase));
            var actual = WindowsOwnedDirectory.Capture(parent);
            if (owned != null)
            {
                if (!owned.Present || actual != owned.Identity) throw new IOException("The source directory parent changed outside the editor.");
            }
            else if (!existingParents.TryGetValue(parent, out var expected) || actual != expected)
                throw new IOException("The source directory parent changed outside the editor.");
            if (IsRoot(parent)) break;
            parent = Path.GetDirectoryName(parent);
        }
    }
    private OwnedDirectoryIdentity ExpectedParent(string directory)
    {
        var parent = Path.GetDirectoryName(directory) ?? throw new IOException("The source directory parent is unavailable.");
        var owned = created.FirstOrDefault(receipt => string.Equals(receipt.Path, parent, StringComparison.OrdinalIgnoreCase));
        if (owned != null)
        {
            if (!owned.Present) throw new IOException("The source directory parent has unfinished removal.");
            return owned.Identity;
        }
        return existingParents.TryGetValue(parent, out var expected) ? expected
            : throw new IOException("The source directory parent ownership is unavailable.");
    }
    private static void ValidateAbsent(string path)
    {
        // Exists hides access errors. Only genuine missing-path errors establish absence.
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        throw new IOException("The restored source directory path is occupied.");
    }
    internal void ValidateOwnedDirectories()
    {
        foreach (var directory in created)
        {
            ValidatePath(directory.Path);
            if (!directory.Present) { ValidateAbsent(directory.Path); continue; }
            ValidateParentChain(directory.Path);
            if (WindowsOwnedDirectory.Capture(directory.Path) != directory.Identity)
                throw new IOException("The source directory changed outside the editor.");
        }
    }
    internal void RetainExisting(string directory, OwnedDirectoryIdentity expected)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        ValidatePath(directory);
        if (IsRoot(directory)) throw new IOException("The source directory root cannot be removed.");
        if (created.Any(receipt => string.Equals(receipt.Path, directory, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The owned directory is already captured.");
        if (WindowsOwnedDirectory.Capture(directory) != expected)
            throw new IOException("The code directory changed outside the editor.");
        var parent = Path.GetDirectoryName(directory);
        while (parent != null)
        {
            ValidatePath(parent);
            var actual = WindowsOwnedDirectory.Capture(parent);
            var owned = created.FirstOrDefault(receipt => string.Equals(receipt.Path, parent, StringComparison.OrdinalIgnoreCase));
            if (owned != null)
            {
                if (!owned.Present || owned.Identity != actual) throw new IOException("The source directory parent changed outside the editor.");
            }
            else
            {
                if (existingParents.TryGetValue(parent, out var previous) && previous != actual)
                    throw new IOException("The source directory parent changed outside the editor.");
                existingParents[parent] = actual;
            }
            if (IsRoot(parent)) break;
            parent = Path.GetDirectoryName(parent);
        }
        created.Add(new(directory, expected));
    }
    internal void Ensure(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        ValidatePath(directory);
        var owned = created.FirstOrDefault(receipt => string.Equals(receipt.Path, directory, StringComparison.OrdinalIgnoreCase));
        if (Directory.Exists(directory))
        {
            var actual = WindowsOwnedDirectory.Capture(directory);
            if (owned != null && (!owned.Present || actual != owned.Identity))
                throw new IOException("The source directory changed outside the editor.");
            if (owned == null)
            {
                if (existingParents.TryGetValue(directory, out var expected) && actual != expected)
                    throw new IOException("The source directory parent changed outside the editor.");
                existingParents[directory] = actual;
            }
            var parent = Path.GetDirectoryName(directory);
            if (parent != null && !IsRoot(directory)) Ensure(parent);
            return;
        }
        if (owned != null) throw new IOException("The owned source directory has unfinished removal.");
        if (File.Exists(directory)) throw new IOException("The source directory path is occupied.");
        if (IsRoot(directory)) throw new IOException("The source directory root is unavailable.");
        Ensure(Path.GetDirectoryName(directory)!);
        ValidateParentChain(directory);
        created.Add(new(directory, WindowsOwnedDirectory.Create(directory, ExpectedParent(directory))));
    }
    internal void CancelInitial() => Remove();
    internal void UndoOwnedDirectories() => Remove();
    internal void RedoOwnedDirectories() => Rollforward();
    private void RemoveOne(DirectoryReceipt directory)
    {
        ValidateParentChain(directory.Path);
        WindowsOwnedDirectory.Remove(directory.Path, directory.Identity);
        directory.Present = false;
    }
    private void RestoreOne(DirectoryReceipt directory)
    {
        ValidateParentChain(directory.Path);
        ValidateAbsent(directory.Path);
        var identity = WindowsOwnedDirectory.Create(directory.Path, ExpectedParent(directory.Path));
        directory.Identity = identity;
        directory.Present = true;
    }
    private void Remove()
    {
        ValidateOwnedDirectories();
        var removed = new List<DirectoryReceipt>();
        try
        {
            for (var index = created.Count - 1; index >= 0; index--)
            {
                var directory = created[index];
                if (!directory.Present) continue;
                RemoveOne(directory);
                removed.Add(directory);
            }
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            foreach (var directory in removed.AsEnumerable().Reverse())
                try { RestoreOne(directory); }
                catch (Exception error) { errors.Add(error); }
            if (errors.Count > 1) throw new AggregateException("The source directory removal could not be restored.", errors);
            throw;
        }
    }
    private void Rollforward()
    {
        ValidateOwnedDirectories();
        var restored = new List<DirectoryReceipt>();
        try
        {
            foreach (var directory in created)
            {
                if (directory.Present) continue;
                RestoreOne(directory);
                restored.Add(directory);
            }
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            foreach (var directory in restored.AsEnumerable().Reverse())
                try { RemoveOne(directory); }
                catch (Exception error) { errors.Add(error); }
            if (errors.Count > 1) throw new AggregateException("The source directory restoration could not be reverted.", errors);
            throw;
        }
    }
}
