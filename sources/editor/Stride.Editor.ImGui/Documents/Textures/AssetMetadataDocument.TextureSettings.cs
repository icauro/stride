using Stride.Assets.Textures;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Diagnostics;
using Stride.Core.Extensions;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private AssetItem? textureSettingsItem;
    private AssetPropertyGraph? textureSettingsGraph;
    private readonly LoggerResult textureSettingsLog = new();
    private IReadOnlyList<TextureSettingField> textureSettingsFields = [];
    private TextureSettingField? activeTextureSetting;
    private EditorDeferredAssetMutation? textureSettingMutation;
    private bool textureSettingMutationSettled;
    private string? textureSettingEditTitle;
    private long textureSettingsRevision, savedTextureSettingsRevision;
    private long textureSettingsChangeVersion;
    public TextureAsset? TextureSettings => AuthoredAsset as TextureAsset;
    public bool CanEditTextureSettings => CanEditArchetypeAsset && TextureSettings is { Type: not null };
    public long SavedTextureSettingsRevision => savedTextureSettingsRevision;
    public long TextureSettingsSaveVersion { get; private set; }
    public long TextureSettingsEditVersion => textureSettingsChangeVersion;
    private bool HasTextureSettingChange => textureSettingMutation?.HasChanges == true;
    public static IReadOnlyList<Type> TextureTypes { get; } = [typeof(ColorTextureType), typeof(NormapMapTextureType), typeof(GrayscaleTextureType)];
    public IReadOnlyList<TextureSettingField> TextureSettingsFields
    {
        get { EnsureTextureSettingsGraph(); return textureSettingsFields; }
    }
    internal IMemberNode? TextureTypeSettingNode
    { get { EnsureTextureSettingsGraph(); return textureSettingsGraph?.RootNode[nameof(TextureAsset.Type)]; } }
    private void EnsureTextureSettingsGraph()
    {
        if (textureSettingsGraph != null || TextureSettings == null) return;
        textureSettingsGraph = EnsureSettingsAuthoringGraph(out textureSettingsItem);
        RefreshTextureSettingsFields();
    }
    private void RefreshTextureSettingsFields()
    {
        var fields = new List<TextureSettingField>();
        foreach (var name in new[] { nameof(TextureAsset.Source), nameof(TextureAsset.IsCompressed), nameof(TextureAsset.Width), nameof(TextureAsset.Height),
            nameof(TextureAsset.IsSizeInPercentage), nameof(TextureAsset.GenerateMipmaps), nameof(TextureAsset.IsStreamable) })
            Add(textureSettingsGraph!.RootNode[name], name);
        if (textureSettingsGraph!.RootNode[nameof(TextureAsset.Type)].Target is { } target)
            foreach (var node in target.Members.Where(node => node.MemberDescriptor.HasSet)) Add(node, "Type." + node.Name);
        textureSettingsFields = fields.OrderBy(field => field.Category, StringComparer.Ordinal).ToArray();
        void Add(IMemberNode node, string key)
        {
            var display = node.MemberDescriptor.GetCustomAttributes<DisplayAttribute>(true).FirstOrDefault();
            if (display?.Browsable == false) return;
            fields.Add(new TextureSettingField(node, key, display?.Name ?? node.Name, display?.Category ?? ""));
        }
    }
    public void BeginTextureSettingEdit(TextureSettingField field)
    {
        EnsureTextureSettingsGraph();
        EnsureEditableTextureSetting(field);
        if (!textureSettingMutationSettled && activeTextureSetting is { } active
            && ReferenceEquals(active.Node, field.Node) && active.Key == field.Key) return;
        EndTextureSettingEdit();
        EnsureEditableTextureSetting(field);
        var mutation = BeginExtensionCommandMutation();
        activeTextureSetting = field; textureSettingMutation = mutation;
        textureSettingMutationSettled = false;
        textureSettingEditTitle = "Edit texture setting: " + field.Node.Name;
    }
    private void EnsureEditableTextureSetting(TextureSettingField field)
    {
        if (!CanEditTextureSettings || !textureSettingsFields.Any(current => current.Key == field.Key && ReferenceEquals(current.Node, field.Node))
            || !CanEditAssetSettingNode(field.Node) || !CanAssignAuthoringNode(field.Node) || IsExtensionPropertyReadOnly(field.Node))
            throw new InvalidOperationException("This asset can't be edited.");
    }
    public void UpdateTextureSetting(TextureSettingField field, object? value)
    {
        if (value == null ? field.Type.IsValueType && Nullable.GetUnderlyingType(field.Type) == null
            || field.Node.MemberDescriptor.GetCustomAttributes<Stride.Core.Annotations.NotNullAttribute>(true).Any()
            || field.Node.MemberDescriptor.GetCustomAttributes<System.Diagnostics.CodeAnalysis.NotNullAttribute>(true).Any()
            : !(Nullable.GetUnderlyingType(field.Type) ?? field.Type).IsInstanceOfType(value))
            throw new ArgumentException("The texture setting value has an incompatible type.");
        PropertyValueValidation.Normalize(value, null);
        if (field.Key is nameof(TextureAsset.Width) or nameof(TextureAsset.Height))
            value = Math.Clamp((float)value!, 0, TextureSettings!.IsSizeInPercentage ? 100 : float.MaxValue);
        if (value is UFile source && source.ToString().IndexOfAny(['\0', '\r', '\n']) >= 0) throw new ArgumentException("Invalid source path.");
        BeginTextureSettingEdit(field);
        if (Equals(field.Node.Retrieve(), value)) return;
        try { field.Node.Update(value); textureSettingsChangeVersion++; }
        catch (Exception failure)
        {
            try { CancelTextureSettingEdit(); }
            catch (Exception recovery) { throw new AggregateException("Texture changes and restoration failed.", failure, recovery); }
            throw;
        }
    }
    public void EndTextureSettingEdit()
    {
        if (textureSettingMutation is not { } mutation) return;
        if (!textureSettingMutationSettled)
        {
            try { mutation.Commit(textureSettingEditTitle!); textureSettingMutationSettled = true; }
            catch (Exception failure)
            {
                try { CancelTextureSettingEdit(); }
                catch (Exception recovery) { throw new AggregateException("Texture changes and restoration failed.", failure, recovery); }
                throw;
            }
        }
        ReleaseTextureSettingMutation(mutation);
    }
    public void CancelTextureSettingEdit()
    {
        if (textureSettingMutation is not { } mutation) return;
        if (!textureSettingMutationSettled) { mutation.Rollback(); textureSettingMutationSettled = true; }
        ReleaseTextureSettingMutation(mutation);
    }
    private void ReleaseTextureSettingMutation(EditorDeferredAssetMutation mutation)
    {
        // Retain the journal and its actual owner until every cleanup phase succeeds.
        mutation.Dispose();
        textureSettingMutation = null; activeTextureSetting = null;
        textureSettingEditTitle = null; textureSettingMutationSettled = false;
    }
    public void ChangeTextureType(Type type)
    {
        var entry = NativeAbstractEntries.TypeEntry(TextureTypeEntries(), type)
            ?? throw new InvalidOperationException("This texture type cannot be selected.");
        ChangeTextureTypeEntry(entry);
    }
    private void PrepareTextureSettingsForSave()
    {
        if (textureSettingsGraph == null) return;
        textureSettingsGraph.PrepareForSave(textureSettingsLog, textureSettingsItem!);
        if (textureSettingsLog.HasErrors) throw new InvalidOperationException(textureSettingsLog.ToText());
    }
    private void MarkTextureSettingsSaved()
    {
        if (textureSettingsRevision != 0 || savedTextureSettingsRevision != 0) TextureSettingsSaveVersion++;
        savedTextureSettingsRevision = textureSettingsRevision;
    }
}
