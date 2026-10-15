using Stride.Assets.SpriteFont;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Extensions;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private AssetPropertyGraph? fontSettingsGraph;
    private readonly HashSet<FontSettingField> fontFields = [];
    private IReadOnlyList<FontSettingField> fontSettingFields = [];
    private FontSettingField? activeFontSetting;
    private object? fontSettingStart;
    private object? fontSettingPending;
    private long fontSettingStartRevision;
    private bool committingFontSetting;
    public bool IsFontAsset => AuthoredAsset is SpriteFontAsset or PrecompiledSpriteFontAsset;
    public bool CanEditFontSettings => IsFontAsset && CanEditArchetypeAsset;
    public long FontSettingsEditVersion { get; private set; }
    private bool HasFontSettingChange => activeFontSetting != null && !Equals(fontSettingStart, fontSettingPending);
    public IReadOnlyList<FontSettingField> FontSettingsFields { get { EnsureFontSettingsGraph(); return fontSettingFields; } }
    private void EnsureFontSettingsGraph()
    {
        if (!IsFontAsset || fontSettingsGraph != null) return;
        fontSettingsGraph = EnsureSettingsAuthoringGraph(out _); RefreshFontSettingsFields();
    }
    private void RefreshFontSettingsFields()
    {
        fontFields.Clear(); fontSettingFields = ReadFontFields(fontSettingsGraph!.RootNode, "Font", 0, new HashSet<IObjectNode>());
    }
    private IReadOnlyList<FontSettingField> ReadFontFields(IObjectNode target, string path, int depth, HashSet<IObjectNode> visited)
    {
        // Unknown provider/custom values retain their native ParsingEvents;
        // reflected proxy base members are not an editable authored subtree.
        if (target.Retrieve() is Stride.Core.Yaml.IUnloadable || depth > 12 || !visited.Add(target)) return [];
        var result = new List<FontSettingField>();
        foreach (var node in target.Members)
        {
            if (depth == 0 && node.Name is nameof(Asset.Id) or nameof(Asset.Tags) or nameof(Asset.Archetype)) continue;
            var display = node.MemberDescriptor.GetCustomAttributes<DisplayAttribute>(true).FirstOrDefault();
            if (display?.Browsable == false) continue;
            var member = new FontSettingField(node, path + "." + node.Name, display?.Name ?? node.Name, display?.Category ?? "");
            if (member.Collection is { } collection)
                member.Items = (collection.Indices ?? []).Select(index => new FontSettingItem(collection.IndexToId(index), index, collection.Retrieve(index),
                    collection.ItemReferences != null && collection.IndexedTarget(index) is { } item ? ReadFontFields(item, member.Key + "/" + collection.IndexToId(index), depth + 1, visited) : [])).ToArray();
            else if (!IsFontScalar(node.Type) && !ContentAssetReference.Supports(node.Type) && node.Target is { } child)
                member.Children = ReadFontFields(child, member.Key, depth + 1, visited);
            result.Add(member); fontFields.Add(member);
        }
        visited.Remove(target); return result;
    }
    private static bool IsFontScalar(Type type) => type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(Stride.Core.IO.UFile)
        || type == typeof(Stride.Core.Mathematics.Vector2) || type == typeof(Stride.Core.Mathematics.Rectangle);
    private bool IsActiveFontSetting(FontSettingField member) => activeFontSetting is { } active
        && ReferenceEquals(active.Node, member.Node) && active.Key == member.Key;
    public object? ReadFontSetting(FontSettingField member) => IsActiveFontSetting(member) ? fontSettingPending : member.Node.Retrieve();
    internal bool IsCurrentFontSettingField(FontSettingField member)
    {
        EnsureFontSettingsGraph();
        // fontFields contains actual nested provider, region and glyph members as well as root fields.
        return fontFields.Any(current => ReferenceEquals(current.Node, member.Node) && current.Key == member.Key);
    }
    public bool CanEditFontSetting(FontSettingField member) => CanEditFontSettings
        && IsCurrentFontSettingField(member)
        && CanEditAssetSettingNode(member.Node) && CanAssignAuthoringNode(member.Node) && !IsExtensionPropertyReadOnly(member.Node);
    public void BeginFontSettingEdit(FontSettingField member)
    {
        if (!CanEditFontSetting(member)) throw new InvalidOperationException("This font setting cannot be edited.");
        if (IsActiveFontSetting(member)) { ValidateFontSettingBaseline(member); return; }
        CommitEdits();
        if (!CanEditFontSetting(member)) throw new InvalidOperationException("This font setting cannot be edited.");
        activeFontSetting = member; fontSettingStart = fontSettingPending = member.Node.Retrieve();
        fontSettingStartRevision = revision;
    }
    public void UpdateFontSetting(FontSettingField member, object? value)
    {
        if (value == null && !member.AllowsNull || value != null && !member.Type.IsInstanceOfType(value)) throw new ArgumentException("The font setting value has an incompatible type.");
        value = NormalizeFontSetting(member, value);
        if (value is Stride.Core.IO.UFile file && !string.IsNullOrEmpty(file)) System.IO.Path.GetFullPath(file, System.IO.Path.GetDirectoryName(Path)!);
        BeginFontSettingEdit(member);
        fontSettingPending = value;
    }
    public void EndFontSettingEdit()
    {
        if (activeFontSetting == null || committingFontSetting) return;
        var member = activeFontSetting; var before = fontSettingStart; var after = fontSettingPending;
        if (ContentAssetReference.AreEqual(before, after)) { CancelFontSettingEdit(); return; }
        ValidateFontSettingBaseline(member);
        // Stage transient widget text until commit. The existing complete graph
        // journal records the value/object change, New/Sealed flags and actual
        // propagated participants together and compensates failed publication.
        committingFontSetting = true;
        try
        {
            ApplyInheritedAssetMutation(() =>
            {
                // CommitEdits inside the native journal may settle another local control first.
                ValidateFontSettingBaseline(member);
                member.Node.Update(after);
            }, "Edit font setting: " + member.Label);
            ClearFontSettingDraft();
        }
        finally { committingFontSetting = false; }
    }
    private void ValidateFontSettingBaseline(FontSettingField member)
    {
        if (!CanEditFontSetting(member) || !IsActiveFontSetting(member) || revision != fontSettingStartRevision
            || !ContentAssetReference.AreEqual(member.Node.Retrieve(), fontSettingStart))
            throw new InvalidOperationException("The authored font setting changed while it was being edited.");
    }
    public void CancelFontSettingEdit()
    {
        if (committingFontSetting) throw new InvalidOperationException("The font setting is being committed.");
        // Widget values are staged; cancellation does not write or reset the canonical graph.
        ClearFontSettingDraft();
    }
    private void ClearFontSettingDraft()
    {
        activeFontSetting = null; fontSettingStart = fontSettingPending = null; fontSettingStartRevision = 0;
    }
    public void ChangeFontSettingType(FontSettingField member, Type type)
    {
        var entry = NativeAbstractEntries.TypeEntry(FontSettingEntries(member), type)
            ?? throw new InvalidOperationException("This font type cannot be selected.");
        ChangeFontSettingEntry(member, entry);
    }
    public void AddFontSettingItem(FontSettingField member)
        => AddFontSettingItem(member, null);
    public void RemoveFontSettingItem(FontSettingField member, FontSettingItem item)
    {
        CommitEdits();
        var collection = member.Collection ?? throw new InvalidOperationException("This setting is not a collection.");
        if (!CanRemoveFontItem(member, item) || !TryGetFontItemAccessor(member, item, out var accessor)) throw new InvalidOperationException("This font item cannot be edited.");
        ApplyInheritedAssetMutation(() => collection.Remove(collection.Retrieve(accessor.Index), accessor.Index), "Remove font item");
    }
}
