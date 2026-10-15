namespace Stride.Editor.ImGui;

internal sealed partial class SceneComponentGizmos
{
    private readonly HashSet<string> pendingNativeHiddenNames = new(StringComparer.Ordinal);
    internal void RestoreNativeSettings(double requestedSize, bool requestedFixedSize, IEnumerable<string> hiddenNames)
    {
        if (double.IsFinite(requestedSize) && requestedSize > 0 && float.IsFinite((float)requestedSize)) size = (float)requestedSize;
        fixedSize = requestedFixedSize; hidden.Clear(); pendingNativeHiddenNames.Clear();
        foreach (var name in hiddenNames)
        {
            var family = Families.FirstOrDefault(candidate => candidate.Name == name);
            if (family != null) hidden.Add(family.Id);
            else if (name != null) pendingNativeHiddenNames.Add(name);
        }
    }
    private void ResolveNativeHiddenFamilies()
    {
        foreach (var family in Families)
            if (pendingNativeHiddenNames.Remove(family.Name)) hidden.Add(family.Id);
    }
    internal string[] CaptureNativeHiddenGizmos(IEnumerable<string> originalNames)
    {
        var families = Families;
        var names = new List<string>();
        foreach (var family in families) if (!IsVisible(family)) names.Add(family.Name);
        foreach (var name in originalNames)
            if (name != null && !families.Any(family => family.Name == name)) names.Add(name);
        foreach (var name in pendingNativeHiddenNames)
            if (!families.Any(family => family.Name == name) && !names.Contains(name, StringComparer.Ordinal)) names.Add(name);
        return names.ToArray();
    }
}
