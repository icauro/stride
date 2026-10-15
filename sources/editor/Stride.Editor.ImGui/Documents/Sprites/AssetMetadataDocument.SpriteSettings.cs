using Stride.Assets.Sprite;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Diagnostics;
using Stride.Core.Extensions;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Core.Transactions;
using Stride.Assets.Presentation.NodePresenters.Rules;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private AssetItem? spriteSettingsItem;
    private AssetPropertyGraph? spriteSettingsGraph;
    private readonly LoggerResult spriteSettingsLog = new();
    private IReadOnlyList<SpriteSettingField> spriteSettingsFields = [];
    private IReadOnlyList<SpriteSettingFrame> spriteSettingsFrames = [];
    private readonly Dictionary<SpriteSettingField, SpriteSettingFrame?> spriteSettingOwners = [];
    private SpriteSettingField? activeSpriteSetting;
    private object? spriteSettingStart;
    private IMemberNode? spriteLockedBorderNode;
    private object? spriteLockedBorderStart;
    private EditorDeferredAssetMutation? spriteSettingMutation;
    private string? spriteSettingEditTitle;
    public SpriteSheetAsset? SpriteSettings => AuthoredAsset as SpriteSheetAsset;
    public bool CanEditSpriteSettings => CanEditArchetypeAsset && SpriteSettings != null;
    internal bool CanEditSpriteCollection
    { get { EnsureSpriteSettingsGraph(); return CanEditSpriteSettings && spriteSettingsGraph?.RootNode[nameof(SpriteSheetAsset.Sprites)].Target is { } sprites && CanEditAssetSettingNode(sprites); } }
    public long SpriteSettingsEditVersion { get; private set; }
    private bool HasSpriteSettingChange => spriteSettingMutation?.HasChanges == true || activeSpriteSetting != null && (!Equals(spriteSettingStart, activeSpriteSetting.Node.Retrieve())
        || spriteLockedBorderNode != null && !Equals(spriteLockedBorderStart, spriteLockedBorderNode.Retrieve()));
    public IReadOnlyList<SpriteSettingField> SpriteSettingsFields { get { EnsureSpriteSettingsGraph(); return spriteSettingsFields; } }
    public IReadOnlyList<SpriteSettingFrame> SpriteSettingsFrames { get { EnsureSpriteSettingsGraph(); return spriteSettingsFrames; } }
    internal bool IsEditingSpriteSetting(IMemberNode member) => ReferenceEquals(activeSpriteSetting?.Node, member);

    private void EnsureSpriteSettingsGraph()
    {
        if (spriteSettingsGraph != null || SpriteSettings == null) return;
        spriteSettingsGraph = EnsureSettingsAuthoringGraph(out spriteSettingsItem);
        RefreshSpriteSettingsFields();
    }
    private void RefreshSpriteSettingsFields()
    {
        var root = spriteSettingsGraph!.RootNode;
        var fields = new List<SpriteSettingField>();
        // Discover the registered authored type, including subclass members. Sprites is
        // presented by the frame list, exactly as the original SpriteSheet node updater.
        foreach (var node in root.Members.Where(node => node.Name != nameof(SpriteSheetAsset.Sprites)
            && node.MemberDescriptor.HasSet && !node.MemberDescriptor.GetCustomAttributes<DataMemberIgnoreAttribute>(true).Any()))
            fields.Add(Field(node, node.Name, node.MemberDescriptor.GetCustomAttributes<DisplayAttribute>(true).FirstOrDefault()?.Category ?? ""));
        if (root[nameof(SpriteSheetAsset.Packing)].Target is { } packing)
            foreach (var node in packing.Members.Where(node => node.MemberDescriptor.HasSet
                && !node.MemberDescriptor.GetCustomAttributes<DataMemberIgnoreAttribute>(true).Any()))
                fields.Add(Field(node, "Packing." + node.Name, "Atlas Packing"));
        spriteSettingsFields = fields;
        var frames = new List<SpriteSettingFrame>();
        var sprites = (IAssetObjectNode)root[nameof(SpriteSheetAsset.Sprites)].Target!;
        foreach (var index in sprites.Indices)
        {
            if (sprites.IndexedTarget(index) is not { } target || sprites.Retrieve(index) is not SpriteInfo value) continue;
            var id = sprites.IndexToId(index);
            var frameFields = new List<SpriteSettingField>();
            foreach (var node in target.Members.Where(node => node.MemberDescriptor.HasSet))
            {
                if (!SpriteSheetNodeRules.IsFrameMemberVisible(SpriteSettings!.Type, node.Name)) continue;
                frameFields.Add(Field(node, id + "." + node.Name, ""));
            }
            frames.Add(new(id, value, frameFields, target[nameof(SpriteInfo.Borders)]));
        }
        spriteSettingsFrames = frames;
        spriteSettingOwners.Clear();
        foreach (var rootMember in spriteSettingsFields) spriteSettingOwners.Add(rootMember, null);
        foreach (var frame in spriteSettingsFrames) foreach (var member in frame.Fields) spriteSettingOwners.Add(member, frame);
        SpriteFieldsVersion++;
        ReconcileSpriteSelection();
        static SpriteSettingField Field(IMemberNode node, string key, string category)
        {
            var display = node.MemberDescriptor.GetCustomAttributes<DisplayAttribute>(true).FirstOrDefault();
            return new(node, key, display?.Name ?? Stride.Core.Presentation.Core.Utils.SplitCamelCase(node.Name), category);
        }
    }
    public void BeginSpriteSettingEdit(SpriteSettingField field)
        => BeginSpriteSettingEdit(field, "Edit sprite setting: " + field.Label);
    internal void BeginSpriteSettingEdit(SpriteSettingField field, string title)
    {
        EnsureSpriteSettingsGraph();
        if (!CanEditSpriteSetting(field))
            throw new InvalidOperationException("This sprite setting can't be edited.");
        if (activeSpriteSetting is { } active && ReferenceEquals(active.Node, field.Node) && active.Key == field.Key) return;
        EndSpriteSettingEdit();
        var mutation = BeginExtensionCommandMutation();
        activeSpriteSetting = field; spriteSettingMutation = mutation; spriteSettingStart = field.Node.Retrieve(); spriteSettingEditTitle = title;
        if (field.Node.Name == nameof(SpriteInfo.TextureRegion))
            foreach (var frame in spriteSettingsFrames)
                if (frame.Fields.Any(member => ReferenceEquals(member.Node, field.Node)) && IsSpriteBordersLocked(frame.Id))
                {
                    spriteLockedBorderNode = frame.BorderNode;
                    spriteLockedBorderStart = spriteLockedBorderNode?.Retrieve(); break;
                }
    }
    internal bool CanEditSpriteSetting(SpriteSettingField field)
    {
        if (!CanEditSpriteSettings || !CanEditAssetSettingNode(field.Node) || !CanAssignAuthoringNode(field.Node)
            || IsExtensionPropertyReadOnly(field.Node)) return false;
        if (!TryGetSpriteSettingOwner(field, out var frame)) return false;
        if (field.Node.Name == nameof(SpriteInfo.TextureRegion) && frame != null && IsSpriteBordersLocked(frame.Id))
            if (!CanEditAssetSettingNode(frame.BorderNode) || !CanAssignAuthoringNode(frame.BorderNode)
                || IsExtensionPropertyReadOnly(frame.BorderNode)) return false;
        return true;
    }
    private bool TryGetSpriteSettingOwner(SpriteSettingField field, out SpriteSettingFrame? frame)
    {
        if (!spriteSettingOwners.TryGetValue(field, out frame))
        {
            var current = spriteSettingOwners.Keys.FirstOrDefault(member => member.Key == field.Key && ReferenceEquals(member.Node, field.Node));
            if (current == null) return false;
            frame = spriteSettingOwners[current];
        }
        return true;
    }
    public void UpdateSpriteSetting(SpriteSettingField field, object value)
        => UpdateSpriteSetting(field, value, true);
    internal void UpdateSpriteCanvasSetting(SpriteSettingField field, object value)
        => UpdateSpriteSetting(field, value, false);
    private void UpdateSpriteSetting(SpriteSettingField field, object value, bool numericPresentation)
    {
        if (!(Nullable.GetUnderlyingType(field.Type) ?? field.Type).IsInstanceOfType(value)) throw new ArgumentException("The sprite setting value has an incompatible type.");
        value = numericPresentation ? NormalizeSpriteSetting(field, value, field.Node.Retrieve()) : PropertyValueValidation.Normalize(value, null)!;
        if (value is UFile source && source.ToString().IndexOfAny(['\0', '\r', '\n']) >= 0) throw new ArgumentException("Invalid source path.");
        if (value is Rectangle region) value = ClampSpriteSettingRegion(field, region);
        BeginSpriteSettingEdit(field);
        if (!Equals(field.Node.Retrieve(), value))
        {
            var previous = field.Node.Retrieve();
            try
            {
            field.Node.Update(value);
            if (spriteLockedBorderNode != null && previous is Rectangle oldRegion && value is Rectangle nextRegion
                && (oldRegion.Width != nextRegion.Width || oldRegion.Height != nextRegion.Height))
            {
                var beforeBorders = (Vector4)spriteLockedBorderNode.Retrieve()!;
                var borders = beforeBorders + new Vector4(oldRegion.X - nextRegion.X, oldRegion.Y - nextRegion.Y,
                    nextRegion.X + nextRegion.Width - oldRegion.X - oldRegion.Width, nextRegion.Y + nextRegion.Height - oldRegion.Y - oldRegion.Height);
                borders.X = Math.Clamp(borders.X, 0, Math.Max(0, nextRegion.Width - beforeBorders.Z));
                borders.Y = Math.Clamp(borders.Y, 0, Math.Max(0, nextRegion.Height - beforeBorders.W));
                borders.Z = Math.Clamp(borders.Z, 0, Math.Max(0, nextRegion.Width - beforeBorders.X));
                borders.W = Math.Clamp(borders.W, 0, Math.Max(0, nextRegion.Height - beforeBorders.Y));
                spriteLockedBorderNode.Update(borders);
            }
            SpriteSettingsEditVersion++;
            }
            catch (Exception failure)
            {
                try { CancelSpriteSettingEdit(); }
                catch (Exception recovery) { throw new AggregateException("Sprite changes and restoration failed.", failure, recovery); }
                throw;
            }
        }
    }
    public void EndSpriteSettingEdit()
    {
        if (activeSpriteSetting == null) return;
        var field = activeSpriteSetting; var before = spriteSettingStart; var after = field.Node.Retrieve();
        var borderNode = spriteLockedBorderNode; var beforeBorders = spriteLockedBorderStart; var afterBorders = borderNode?.Retrieve();
        var mutation = spriteSettingMutation!;
        CommitSpriteMutation(mutation, spriteSettingEditTitle!, () =>
        {
            spriteSettingMutation = null;
            spriteLockedBorderNode = null; spriteLockedBorderStart = null;
            activeSpriteSetting = null; spriteSettingStart = null;
            spriteSettingEditTitle = null;
        });
        if (Equals(before, after) && Equals(beforeBorders, afterBorders)) return;
        SpriteSourceAssigned(field.Node, before, after);
        if (field.Node.Name == nameof(SpriteSheetAsset.Type)) RefreshSpriteSettingsFields();
    }
    private IAssetObjectNode EditableSprites()
    {
        EnsureSpriteSettingsGraph(); CommitEdits();
        if (!CanEditSpriteSettings) throw new InvalidOperationException("This sprite sheet can't be edited.");
        var node = (IAssetObjectNode)spriteSettingsGraph!.RootNode[nameof(SpriteSheetAsset.Sprites)].Target!;
        if (!CanEditAssetSettingNode(node)) throw new InvalidOperationException("These sprites cannot be edited.");
        return node;
    }
    public void AddSpriteFrame()
    {
        var sprites = EditableSprites();
        var value = new SpriteInfo { Name = NamingHelper.ComputeNewName("New sprite", SpriteSettings!.Sprites, sprite => sprite.Name), PixelsPerUnit = 100, CenterFromMiddle = true };
        AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
        var index = new NodeIndex(SpriteSettings!.Sprites.Count);
        ApplyInheritedAssetMutation(() => sprites.Add(value, index), "Add new sprite");
        var id = sprites.IndexToId(index);
        SelectSpriteFrame(id);
    }
    public void RemoveSpriteFrame(SpriteSettingFrame frame)
    {
        var sprites = EditableSprites();
        if (!sprites.HasId(frame.Id)) throw new InvalidOperationException("This sprite no longer exists.");
        var index = sprites.IdToIndex(frame.Id);
        if (!CanEditAssetSettingItem(sprites, index)) throw new InvalidOperationException("This sprite cannot be edited.");
        ApplyInheritedAssetMutation(() => sprites.Remove(frame.Value, index), "Delete sprite");
    }
    public void MoveSpriteFrame(SpriteSettingFrame frame, int destination)
    {
        var sprites = EditableSprites();
        if (!sprites.HasId(frame.Id) || destination < 0 || destination >= SpriteSettings!.Sprites.Count) throw new ArgumentOutOfRangeException(nameof(destination));
        var source = sprites.IdToIndex(frame.Id).Int;
        if (!CanEditAssetSettingItem(sprites, new NodeIndex(source))) throw new InvalidOperationException("This sprite cannot be edited.");
        if (source == destination) return;
        ApplyInheritedAssetMutation(() => MoveSpriteFrameCore(sprites, frame, destination), "Move sprite");
    }
    private static void MoveSpriteFrameCore(IAssetObjectNode sprites, SpriteSettingFrame frame, int destination)
    {
        var overridden = sprites.IsItemOverridden(sprites.IdToIndex(frame.Id));
        sprites.RemoveAndDiscard(frame.Value, sprites.IdToIndex(frame.Id), frame.Id);
        sprites.Restore(frame.Value, new NodeIndex(destination), frame.Id);
        sprites.OverrideItem(overridden, new NodeIndex(destination));
    }
    private void PrepareSpriteSettingsForSave()
    {
        if (spriteSettingsGraph == null) return;
        spriteSettingsGraph.PrepareForSave(spriteSettingsLog, spriteSettingsItem!);
        if (spriteSettingsLog.HasErrors) throw new InvalidOperationException(spriteSettingsLog.ToText());
    }
}
