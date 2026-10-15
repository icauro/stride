using Stride.Core.Quantum;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Stride.Assets.Sprite;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private long spriteCommonSelection = -1, spriteCommonFields = -1;
    private SpriteCommonSetting[] spriteCommonSettings = [];
    private SpriteCommonSetting? activeSpriteCommon;
    private (IMemberNode Node, object? Before)[] spriteCommonStart = [];
    private EditorDeferredAssetMutation? spriteCommonMutation;
    private bool spriteCommonHasChange;
    public long SpriteSelectionVersion { get; private set; }
    public long SpriteFieldsVersion { get; private set; }
    private bool HasSpriteCommonChange => spriteCommonHasChange || spriteCommonMutation?.HasChanges == true;
    public IReadOnlyList<SpriteCommonSetting> SpriteCommonSettings
    {
        get
        {
            EnsureSpriteSettingsGraph();
            if (spriteCommonSelection == SpriteSelectionVersion && spriteCommonFields == SpriteFieldsVersion) return spriteCommonSettings;
            var frames = SelectedSpriteIds.Select(id => spriteSettingsFrames.First(frame => frame.Id == id)).ToArray(); var settings = new List<SpriteCommonSetting>();
            if (frames.Length > 1) foreach (var firstMember in frames[0].Fields)
            {
                var members = new List<SpriteSettingField> { firstMember };
                for (var index = 1; index < frames.Length; index++)
                {
                    var match = frames[index].Fields.FirstOrDefault(candidate => candidate.Node.Name == firstMember.Node.Name && candidate.Type == firstMember.Type);
                    if (match == null) break; members.Add(match);
                }
                if (members.Count == frames.Length) settings.Add(new(members.ToArray()));
            }
            spriteCommonSettings = settings.ToArray(); spriteCommonSelection = SpriteSelectionVersion; spriteCommonFields = SpriteFieldsVersion;
            return spriteCommonSettings;
        }
    }
    public void BeginSpriteCommonEdit(SpriteCommonSetting setting)
    {
        if (ReferenceEquals(activeSpriteCommon, setting)) return;
        CommitEdits();
        if (!CanEditSpriteSettings || !SpriteCommonSettings.Any(current => current.Fields.Length == setting.Fields.Length
            && current.Fields.Select((member, index) => member.Key == setting.Fields[index].Key && ReferenceEquals(member.Node, setting.Fields[index].Node)).All(equal => equal))
            || setting.Fields.Any(member => !CanEditSpriteSetting(member)))
            throw new InvalidOperationException("This sprite selection can't be edited.");
        var mutation = BeginExtensionCommandMutation();
        activeSpriteCommon = setting; spriteCommonMutation = mutation;
        spriteCommonHasChange = false;
        var entries = setting.Fields.Select(field => (field.Node, field.Node.Retrieve())).ToList();
        if (setting.First.Node.Name == nameof(SpriteInfo.TextureRegion)) foreach (var frame in spriteSettingsFrames)
            if (selectedSpriteIds.Contains(frame.Id) && IsSpriteBordersLocked(frame.Id))
                entries.Add((frame.BorderNode, frame.BorderNode.Retrieve()));
        spriteCommonStart = entries.ToArray();
    }
    internal bool IsCurrentSpriteCommonSetting(SpriteCommonSetting setting)
        => SpriteCommonSettings.Any(current => current.Fields.Length == setting.Fields.Length
            && current.Fields.Select((field, index) => field.Key == setting.Fields[index].Key && ReferenceEquals(field.Node, setting.Fields[index].Node)).All(equal => equal))
            && setting.Fields.All(IsCurrentSpriteSettingField);
    public void UpdateSpriteCommonValue(SpriteCommonSetting setting, Func<object?, object> transform)
    {
        BeginSpriteCommonEdit(setting);
        var changes = new List<(IMemberNode Node, object? Before, object Value)>();
        try
        {
        if (!IsCurrentSpriteCommonSetting(setting) || setting.Fields.Any(field => !CanEditSpriteSetting(field)))
            throw new InvalidOperationException("This sprite selection can't be edited.");
        foreach (var field in setting.Fields)
        {
            var before = field.Node.Retrieve(); var value = NormalizeSpriteSetting(field, transform(before), before);
            if (value.GetType() != field.Type) throw new ArgumentException("The sprite setting value has an incompatible type.");
            if (value is UFile source && source.ToString().IndexOfAny(['\0', '\r', '\n']) >= 0) throw new ArgumentException("Invalid source path.");
            if (value is Rectangle rectangle) value = ClampSpriteSettingRegion(field, rectangle);
            if (!Equals(before, value)) changes.Add((field.Node, before, value));
            if (before is Rectangle oldRegion && value is Rectangle nextRegion && (oldRegion.Width != nextRegion.Width || oldRegion.Height != nextRegion.Height))
                foreach (var frame in spriteSettingsFrames)
                    if (frame.Fields.Any(member => ReferenceEquals(member.Node, field.Node)) && IsSpriteBordersLocked(frame.Id))
                    {
                        var borders = (Vector4)frame.BorderNode.Retrieve()!;
                        var updated = borders + new Vector4(oldRegion.X - nextRegion.X, oldRegion.Y - nextRegion.Y,
                            nextRegion.X + nextRegion.Width - oldRegion.X - oldRegion.Width, nextRegion.Y + nextRegion.Height - oldRegion.Y - oldRegion.Height);
                        updated.X = Math.Clamp(updated.X, 0, Math.Max(0, nextRegion.Width - borders.Z)); updated.Y = Math.Clamp(updated.Y, 0, Math.Max(0, nextRegion.Height - borders.W));
                        updated.Z = Math.Clamp(updated.Z, 0, Math.Max(0, nextRegion.Width - borders.X)); updated.W = Math.Clamp(updated.W, 0, Math.Max(0, nextRegion.Height - borders.Y));
                        if (updated != borders) changes.Add((frame.BorderNode, borders, updated));
                    }
        }
        foreach (var entry in changes) entry.Node.Update(entry.Value);
        }
        catch (Exception failure)
        {
            try { CancelSpriteCommonEdit(); }
            catch (Exception recovery) { throw new AggregateException("Sprite changes and restoration failed.", failure, recovery); }
            throw;
        }
        if (changes.Count > 0)
        {
            SpriteSettingsEditVersion++; spriteCommonHasChange = false;
            foreach (var entry in spriteCommonStart) if (!Equals(entry.Before, entry.Node.Retrieve())) { spriteCommonHasChange = true; break; }
        }
    }
    public void EndSpriteCommonEdit()
    {
        if (activeSpriteCommon == null) return;
        var setting = activeSpriteCommon; var entries = spriteCommonStart;
        var after = entries.Select(entry => entry.Node.Retrieve()).ToArray();
        CommitSpriteMutation(spriteCommonMutation!, "Edit sprite setting: " + setting.First.Label, () =>
        { spriteCommonMutation = null; activeSpriteCommon = null; spriteCommonStart = []; spriteCommonHasChange = false; });
        if (entries.Select((entry, index) => Equals(entry.Before, after[index])).All(equal => equal)) return;
        for (var index = 0; index < entries.Length; index++) SpriteSourceAssigned(entries[index].Node, entries[index].Before, after[index]);
    }
    public void CancelSpriteCommonEdit()
    {
        if (activeSpriteCommon == null) return;
        spriteCommonMutation!.Rollback(); spriteCommonMutation.Dispose(); spriteCommonMutation = null;
        activeSpriteCommon = null; spriteCommonStart = []; spriteCommonHasChange = false;
    }
}
