using Stride.Assets.Sprite;
using Stride.Core.Mathematics;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Core.Assets.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private readonly Dictionary<ItemId, (string Source, int Width, int Height)> spriteImageSizes = [];
    private readonly Dictionary<ItemId, (string Source, Rectangle Region)> spriteFirstSources = [];
    private void SpriteSourceAssigned(IMemberNode node, object? before, object? after)
    {
        if (node.Name != nameof(SpriteInfo.Source) || !string.IsNullOrEmpty(before?.ToString()) || string.IsNullOrEmpty(after?.ToString())) return;
        foreach (var frame in spriteSettingsFrames)
            if (frame.Fields.Any(member => ReferenceEquals(member.Node, node)))
                spriteFirstSources[frame.Id] = (after.ToString()!, frame.Value.TextureRegion);
    }
    private Rectangle ClampSpriteSettingRegion(SpriteSettingField member, Rectangle region)
    {
        if (TryGetSpriteSettingOwner(member, out var frame) && frame != null
            && spriteImageSizes.TryGetValue(frame.Id, out var size) && size.Source == (frame.Value.Source?.ToString() ?? ""))
            return ClampSpriteRegion(region, size.Width, size.Height, false, false);
        return new(region.X, region.Y, Math.Max(0, region.Width), Math.Max(0, region.Height));
    }
    private static Rectangle ClampSpriteRegion(Rectangle region, int width, int height, bool keepWidth, bool keepHeight)
    {
        var x = Math.Clamp(region.X, 0, Math.Max(0, width - (keepWidth ? region.Width : 0)));
        var y = Math.Clamp(region.Y, 0, Math.Max(0, height - (keepHeight ? region.Height : 0)));
        return new(x, y, Math.Clamp(region.Width, 0, Math.Max(0, width - x)), Math.Clamp(region.Height, 0, Math.Max(0, height - y)));
    }
    public void RefreshSpriteImageSizes(IReadOnlyList<(ItemId Id, string Source, int Width, int Height)> sizes)
    {
        var changes = new List<(SpriteSettingFrame Frame, Rectangle Region)>();
        foreach (var size in sizes)
        {
            var frame = spriteSettingsFrames.FirstOrDefault(candidate => candidate.Id == size.Id);
            if (frame == null || (frame.Value.Source?.ToString() ?? "") != size.Source) continue;
            var known = spriteImageSizes.TryGetValue(size.Id, out var previous);
            spriteImageSizes[size.Id] = (size.Source, size.Width, size.Height);
            var member = frame.Fields.First(candidate => candidate.Node.Name == nameof(SpriteInfo.TextureRegion));
            if (!CanEditSpriteSetting(member)) continue;
            var region = frame.Value.TextureRegion;
            if (spriteFirstSources.Remove(size.Id, out var first) && first.Source == size.Source && first.Region == region)
                region = new(0, 0, size.Width, size.Height);
            else if (known)
            {
                // The WPF size setters clamp only a shrinking axis, keeping region size
                // where possible before reducing it to the new source bounds.
                if (size.Width < previous.Width)
                {
                    var x = Math.Clamp(region.X, 0, Math.Max(0, size.Width - region.Width));
                    region = new(x, region.Y, Math.Clamp(region.Width, 0, Math.Max(0, size.Width - x)), region.Height);
                }
                if (size.Height < previous.Height)
                {
                    var y = Math.Clamp(region.Y, 0, Math.Max(0, size.Height - region.Height));
                    region = new(region.X, y, region.Width, Math.Clamp(region.Height, 0, Math.Max(0, size.Height - y)));
                }
            }
            if (region != frame.Value.TextureRegion) changes.Add((frame, region));
        }
        if (changes.Count > 0) EditSpriteFrames("Update sprite image size", () =>
        {
            foreach (var change in changes) SetSpriteFrameRegion(change.Frame, change.Region);
        });
        foreach (var id in spriteImageSizes.Keys.Where(id => !spriteSettingsFrames.Any(frame => frame.Id == id)).ToArray()) spriteImageSizes.Remove(id);
    }
    public void ApplySpriteSourceChanges(IReadOnlyList<SpriteSettingField> members, Stride.Core.IO.UFile source, int width, int height,
        IReadOnlyList<(int Width, int Height)> previousSizes, bool resetToBase = false)
        => ApplySpriteSourceChanges(members, Enumerable.Repeat<Stride.Core.IO.UFile?>(source, members.Count).ToArray(),
            Enumerable.Repeat((width, height), members.Count).ToArray(), previousSizes, resetToBase);
    internal void ApplySpriteSourceChanges(IReadOnlyList<SpriteSettingField> members, IReadOnlyList<Stride.Core.IO.UFile?> sources,
        IReadOnlyList<(int Width, int Height)> sizes, IReadOnlyList<(int Width, int Height)> previousSizes, bool resetToBase = false, string operationName = "Edit sprite source", IReadOnlyList<INodePresenter>? projections = null)
    {
        if (members.Count != previousSizes.Count || members.Count != sources.Count || members.Count != sizes.Count || projections != null && projections.Count != members.Count)
            throw new ArgumentException("Invalid source image dimensions.");
        var changes = new List<(SpriteSettingField Member, SpriteSettingFrame Frame, Rectangle Region, Stride.Core.IO.UFile? Source, int Width, int Height, INodePresenter? Projection)>();
        for (var index = 0; index < members.Count; index++)
        {
            var member = members[index]; var source = sources[index]; var (width, height) = sizes[index];
            if (member.Node.Name != nameof(SpriteInfo.Source) || !CanEditSpriteSetting(member)
                || !TryGetSpriteSettingOwner(member, out var frame) || frame == null)
                throw new InvalidOperationException("This sprite source cannot be edited.");
            if (resetToBase && (!CanResetAssetSettingNode(member.Node) || ((IAssetNode)member.Node).BaseNode is not IMemberNode inherited
                || !Equals(inherited.Retrieve(), source))) throw new InvalidOperationException("The sprite source base value changed.");
            var region = frame.Value.TextureRegion;
            if (string.IsNullOrEmpty(frame.Value.Source)) region = new(0, 0, width, height);
            else
            {
                var previous = previousSizes[index];
                if (width < previous.Width)
                { var x = Math.Clamp(region.X, 0, Math.Max(0, width - region.Width)); region = new(x, region.Y, Math.Clamp(region.Width, 0, Math.Max(0, width - x)), region.Height); }
                if (height < previous.Height)
                { var y = Math.Clamp(region.Y, 0, Math.Max(0, height - region.Height)); region = new(region.X, y, region.Width, Math.Clamp(region.Height, 0, Math.Max(0, height - y))); }
            }
            var regionMember = frame.Fields.First(candidate => candidate.Node.Name == nameof(SpriteInfo.TextureRegion));
            if (region != frame.Value.TextureRegion && !CanEditSpriteSetting(regionMember)) throw new InvalidOperationException("This sprite region cannot be edited.");
            if (projections != null && (!IsCurrentSpritePresenterGroup(projections) || !CanEditSpritePresenter(projections[index])
                || !ReferenceEquals(projections[index].GetNodeAccessor().Node, member.Node) || projections[index].Type != typeof(Stride.Core.IO.UFile)))
                throw new InvalidOperationException("The sprite source projection changed.");
            changes.Add((member, frame, region, source, width, height, projections?[index]));
        }
        void Apply() => EditSpriteFrames(operationName, () =>
        {
            foreach (var change in changes)
            {
                if (resetToBase) ResetAssetSettingNode(change.Member.Node);
                else if (change.Projection != null) ApplyInheritedAssetMutation(() =>
                {
                    change.Projection.UpdateValue(change.Source!);
                    if (!Equals(change.Member.Node.Retrieve(), change.Source)) throw new InvalidOperationException("The source projector did not apply the decoded source.");
                }, operationName);
                else if (change.Source == null) ApplyInheritedAssetMutation(() => change.Member.Node.Update(null), "Edit sprite source");
                else { UpdateSpriteSetting(change.Member, change.Source); EndSpriteSettingEdit(); }
                spriteFirstSources.Remove(change.Frame.Id);
                spriteImageSizes[change.Frame.Id] = (change.Source?.ToString() ?? "", change.Width, change.Height);
                var current = spriteSettingsFrames.First(frame => frame.Id == change.Frame.Id);
                if (change.Region != current.Value.TextureRegion) SetSpriteFrameRegion(current, change.Region);
            }
        });
        if (SessionHistory == null) Apply();
        else
        {
            using var captured = SessionHistory.CaptureOperationsAtomic(Apply);
            captured.Publish(SessionHistory, operationName);
        }
    }
}
