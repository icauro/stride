using Stride.Assets.Effect;
using System.Runtime.CompilerServices;
using Hexa.NET.ImGui;
using Stride.Core.Quantum;
using Stride.Core.Presentation.Extensions;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class MaterialShaderView { internal string Filter = ""; internal bool PopupOpen; }
    private readonly ConditionalWeakTable<IMemberNode, MaterialShaderView> materialShaderViews = new();
    private void ClearMaterialShaderSettingsState() => materialShaderViews.Clear();
    private IDictionary<string, string> CaptureMaterialShaderSources(AssetMetadataDocument document)
    {
        var item = workspace!.Session.FindAsset(document.AuthoredAsset.Id)
            ?? throw new InvalidOperationException("The material has no owning package.");
        var packages = new[] { item.Package }.Concat(item.Package.Container.FlattenedDependencies
            .Select(dependency => dependency.Package).Where(package => package != null)).Distinct();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var shader in packages.SelectMany(package => package.Assets).Where(asset => asset.Asset is EffectShaderAsset))
        {
            var owner = authoring?.Find(shader.Id)?.Document;
            var text = owner is ScriptSourceDocument script ? script.EditorText
                : owner is IAssetTagDocument tagged && tagged.AuthoredAsset is EffectShaderAsset current ? current.Text : ((EffectShaderAsset)shader.Asset).Text;
            result[System.IO.Path.GetFileName(shader.Location.ToString())] = text;
        }
        return result;
    }
    private bool TryDrawMaterialShaderReference(AssetMetadataDocument document, IMemberNode node, string label, object? value,
        Action<object?> stage, Action finish, bool drawLabel = true, Func<bool>? canEdit = null, string? controlPath = null)
    {
        if (!IsMaterialShaderReference(document, node)) return false;
        var view = materialShaderViews.GetValue(node, _ => new());
        bool Editable() => !IsAuthoringPipelineUpdating && IsCurrentMetadataControlOwner(document)
            && sessionHistory?.Registry.Find(document.AuthoredAsset.Id) is { IsPublished: true } state && ReferenceEquals(state.Document, document)
            && IsMaterialShaderReference(document, node) && document.CanEditAssetSettingNode(node)
            && CanAssignAuthoringNodeForMaterial(node) && !document.IsExtensionPropertyReadOnly(node) && canEdit?.Invoke() != false;
        object?[] Read()
        {
            var sources = document.MaterialShaderSources?.Invoke() ?? CaptureMaterialShaderSources(document);
            return new object?[] { node.Retrieve(), node, node.Parent.Retrieve(), document.EditRevision, document.SessionHistory?.Extensions?.Version }
                .Concat(sources.OrderBy(pair => pair.Key, StringComparer.Ordinal).SelectMany(pair => new object?[] { pair.Key, pair.Value }))
                .Concat(document.MaterialShaderNames(node).OrderBy(name => name, StringComparer.Ordinal)).ToArray();
        }
        IDisposable Acquire()
        {
            var leases = new List<IDisposable>();
            try
            {
                if (!Editable()) throw new InvalidOperationException("The shader property is no longer editable.");
                leases.Add(AcquirePropertyStringOwner(document));
                if (document.AcquireExtensionCommandTarget(node) is { } target) leases.Add(target);
                return new PropertyStringOwners(leases, [document]);
            }
            catch { foreach (var lease in leases) packageCommandCleanup.Add(lease.Dispose); throw; }
        }
        void Commit(string text)
        {
            if (!Editable()) throw new InvalidOperationException("The shader property is no longer editable.");
            if (Equals(node.Retrieve(), text)) return;
            UseTextureSettingsCommandContext(); stage(text); finish();
        }
        if (drawLabel) Gui.TextUnformatted(label);
        Gui.SetNextItemWidth(Math.Max(1, Gui.GetContentRegionAvail().X - UiScale(26)));
        var inputId = Gui.GetID("##Shader");
        try
        {
            PropertyGrid.Strings.Draw(node, false, Editable, Read, Commit, Acquire, controlLabel: "##Shader",
                input: (string control, string hint, ref string text, ImGuiInputTextFlags flags) =>
                {
                    var submitted = NativeStringInput.DrawWithHint(control, hint.Length == 0 ? EditorLocalization.Text("Select shader...") : hint, ref text, flags);
                    view.Filter = text;
                    return submitted;
                }, deferDeactivation: Gui.IsPopupOpen("##Shaders"));
        }
        catch (Exception error) { statusMessage = error.Message; }
        if (Controls.Enabled) Controls.Record("material-settings/" + (controlPath ?? node.Name) + "/shader");
        DrawAssetSettingResetToBase(document, node);
        Gui.SameLine();
        var open = false;
        using (ImGuiScopes.Disabled(!Editable()))
            if (Gui.BeginCombo("##Shaders", "", Hexa.NET.ImGui.ImGuiComboFlags.NoPreview))
            {
                open = true;
                try
                {
                    var sorter = Comparer<string>.Create((left, right) => MaterialShaderCompare(view.Filter, left, right));
                    foreach (var shader in document.MaterialShaderNames(node).Where(name => MaterialShaderMatches(view.Filter, name)).OrderBy(name => name, sorter))
                        if (Gui.Selectable(shader, shader == view.Filter))
                        {
                            var chosen = shader;
                            Execute(() => PropertyGrid.Strings.ValidateExternal(node, inputId, chosen, Editable, Read, Commit, Acquire));
                        }
                }
                finally { Gui.EndCombo(); }
            }
        if (view.PopupOpen && !open && !Gui.IsKeyPressed(ImGuiKey.Escape))
            Execute(() => PropertyGrid.Strings.ValidatePendingForIdentity(node));
        view.PopupOpen = open;
        return true;
    }
    private static bool IsMaterialShaderReference(AssetMetadataDocument document, IMemberNode node)
        => document.MaterialSettings != null && node.Name == nameof(ComputeShaderClassColor.MixinReference) && node.Type == typeof(string)
        && node.Parent.Retrieve() is ComputeShaderClassColor or ComputeShaderClassScalar;
    private static int MaterialShaderCompare(string token, string left, string right)
    {
        if (!string.IsNullOrWhiteSpace(token))
        {
            var a = left.IndexOf(token, StringComparison.InvariantCultureIgnoreCase);
            var b = right.IndexOf(token, StringComparison.InvariantCultureIgnoreCase);
            if (a == 0 && b > 0) return -1;
            if (b == 0 && a > 0) return 1;
        }
        return string.Compare(left, right, StringComparison.InvariantCultureIgnoreCase);
    }
    private static bool MaterialShaderMatches(string input, string name)
    {
        foreach (var token in input.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            if (name.IndexOf(token, StringComparison.InvariantCultureIgnoreCase) < 0 && !token.MatchCamelCase(name)) return false;
        return true;
    }
    private static bool CanAssignAuthoringNodeForMaterial(IMemberNode node) => AssetMetadataDocument.CanAssignAuthoringNode(node);
}
