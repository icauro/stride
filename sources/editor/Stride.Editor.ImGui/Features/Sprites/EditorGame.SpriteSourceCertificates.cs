namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool SpriteSourceFieldsCurrent(AssetMetadataDocument owner, SpriteSettingField[] fields)
        => fields.Length != 0 && sessionHistory?.Registry.Find(owner.AuthoredAsset.Id) is { IsPublished: true } state
            && ReferenceEquals(state.Document, owner) && fields.All(owner.IsCurrentSpriteSettingField);
    private object?[] ReadSpriteSourceCertificate(AssetMetadataDocument owner, SpriteSettingField[] fields)
        => [.. fields.Select(field => field.Node.Retrieve()), .. fields.SelectMany(field => owner.CaptureSpriteSettingDraft(field, false)),
            .. owner.SelectedSpriteIds.Cast<object?>(), owner.Path, sessionHistory?.Registry.Find(owner.AuthoredAsset.Id)?.Generation,
            sessionHistory?.Extensions?.Version];
    private IDisposable AcquireSpriteSourceCertificate(AssetMetadataDocument owner, SpriteSettingField[] fields)
    {
        var leases = new List<IDisposable>();
        try
        {
            if (!SpriteSourceFieldsCurrent(owner, fields)) throw new InvalidOperationException("The sprite source owner changed.");
            leases.Add(AcquirePropertyStringOwner(owner));
            foreach (var field in fields)
                if (owner.AcquireExtensionCommandTarget(field.Node) is { } target) leases.Add(target);
            return new PropertyStringOwners(leases, [owner]);
        }
        catch (Exception failure)
        {
            var cleanup = new PropertyStringOwners(leases, [owner]);
            try { cleanup.Dispose(); }
            catch (Exception release) { packageCommandCleanup.Add(cleanup.Dispose); throw new AggregateException(failure, release); }
            throw;
        }
    }
}
