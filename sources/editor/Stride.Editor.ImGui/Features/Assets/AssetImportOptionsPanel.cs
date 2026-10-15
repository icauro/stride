using Hexa.NET.ImGui;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Assets.Models;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed class AssetImportOptionsPanel
{
    private AssetImportChoice[] choices = [];
    private (string File, AssetImportChoice[] Choices)[] files = [];
    private int fileIndex;
    private readonly Dictionary<string, AssetImportOptions> fileOptions = new(StringComparer.OrdinalIgnoreCase);
    private AssetImportChoice? selected;
    private readonly Dictionary<Type, bool> outputs = [];
    private (AssetId Id, string Name)[] skeletons = [];
    private AssetId? skeleton;
    private bool deduplicate = true, reuseSkeleton, requestOpen;
    private Action<IReadOnlyDictionary<string, AssetImportOptions>>? import;
    private Type? requiredType;
    internal Action<string>? RecordControl { get; set; }
    internal long AcceptedActions { get; private set; }
    internal long CanceledActions { get; private set; }
    internal AssetImportOptionsInspection Inspect() => new(HasRequest, selected?.Id, selected?.Name,
        outputs.ToDictionary(pair => pair.Key.FullName!, pair => pair.Value), deduplicate, reuseSkeleton, skeleton?.ToString(),
        selected?.SupportsDeduplicateMaterials ?? false, selected?.SupportsReuseSkeleton ?? false, AcceptedActions, CanceledActions);
    internal Action? OnCancelled { get; set; }
    internal bool HasRequest => import != null || requestOpen;
    private bool hasDrawn;
    internal void Open((string File, AssetImportChoice[] Choices)[] files, (AssetId Id, string Name)[] skeletons,
        Action<IReadOnlyDictionary<string, AssetImportOptions>> import, IReadOnlyDictionary<string, AssetImportOptions>? initialOptions = null, Type? requiredType = null)
    {
        this.files = files; this.skeletons = skeletons; this.import = import; this.requiredType = requiredType;
        fileOptions.Clear(); fileIndex = 0;
        foreach (var file in files)
        {
            var choice = file.Choices[0];
            fileOptions.Add(file.File, initialOptions?.GetValueOrDefault(file.File) ?? new AssetImportOptions(choice.Id, new Dictionary<Type, bool>(choice.DefaultOutputs)));
        }
        LoadFile(0); requestOpen = true;
    }
    private void StoreFile()
    {
        if (selected == null || files.Length == 0) return;
        NormalizeOutputs();
        fileOptions[files[fileIndex].File] = new AssetImportOptions(selected.Id, new Dictionary<Type, bool>(outputs), deduplicate, reuseSkeleton ? skeleton : null);
    }
    private void LoadFile(int index)
    {
        fileIndex = index; choices = files[index].Choices;
        var options = fileOptions[files[index].File];
        Select(choices.First(choice => choice.Id == options.ImporterId));
        outputs.Clear(); foreach (var pair in options.Outputs!) outputs.Add(pair.Key, pair.Value);
        deduplicate = options.DeduplicateMaterials; skeleton = options.ReuseSkeleton; reuseSkeleton = skeleton != null;
        NormalizeOutputs();
    }
    private void Select(AssetImportChoice choice)
    {
        selected = choice; outputs.Clear();
        foreach (var pair in choice.DefaultOutputs) outputs.Add(pair.Key, pair.Value);
        deduplicate = true; reuseSkeleton = false; skeleton = null;
        NormalizeOutputs();
    }
    private void NormalizeOutputs()
    {
        if (requiredType != null && requiredType != typeof(ModelAsset))
            foreach (var type in outputs.Keys.ToArray()) outputs[type] = type == requiredType;
        if (requiredType != null && outputs.ContainsKey(requiredType)) outputs[requiredType] = true;
        if (selected?.SupportsReuseSkeleton == true && reuseSkeleton && outputs.ContainsKey(typeof(SkeletonAsset))) outputs[typeof(SkeletonAsset)] = false;
    }
    internal void Draw(Action<Action> execute)
    {
        if (requestOpen) { Gui.OpenPopup("Import assets"); requestOpen = false; }
        using var modal = ImGuiScopes.Modal("Import assets", ImGuiWindowFlags.AlwaysAutoResize);
        if (!modal.DrawContents) { if (hasDrawn) Clear(); return; }
        hasDrawn = true;
        if (selected == null || import == null) { Gui.CloseCurrentPopup(); return; }
        if (files.Length > 1 && Gui.BeginCombo("File", Path.GetFileName(files[fileIndex].File)))
        {
            for (var index = 0; index < files.Length; index++)
            {
                Gui.PushID(files[index].File);
                if (Gui.Selectable(Path.GetFileName(files[index].File), index == fileIndex)) { StoreFile(); LoadFile(index); }
                Gui.PopID();
            }
            Gui.EndCombo();
        }
        if (choices.Length > 1 && Gui.BeginCombo("Importer", selected.Name))
        {
            foreach (var choice in choices) if (Gui.Selectable(choice.Name, ReferenceEquals(choice, selected))) Select(choice);
            Gui.EndCombo();
        }
        foreach (var pair in outputs.ToArray())
        {
            if (pair.Key == requiredType || requiredType != null && requiredType != typeof(ModelAsset)
                || selected.SupportsReuseSkeleton && pair.Key == typeof(SkeletonAsset)) continue;
            var value = pair.Value;
            var label = DisplayAttribute.GetDisplayName(pair.Key) ?? pair.Key.Name;
            Gui.PushID(pair.Key.FullName);
            if (Gui.Checkbox(label, ref value)) outputs[pair.Key] = value;
            RecordControl?.Invoke("fbx/options/output/" + pair.Key.FullName);
            Gui.PopID();
        }
        if (selected.SupportsDeduplicateMaterials)
        {
            Gui.Checkbox(EditorLocalization.Widget("Deduplicate materials", "Button"), ref deduplicate);
            RecordControl?.Invoke("fbx/options/deduplicate");
        }
        if (selected.SupportsReuseSkeleton)
        {
            if (Gui.RadioButton(EditorLocalization.Widget("Import skeleton", "Button"), !reuseSkeleton && outputs.GetValueOrDefault(typeof(SkeletonAsset))))
            { reuseSkeleton = false; outputs[typeof(SkeletonAsset)] = true; }
            RecordControl?.Invoke("fbx/options/skeleton/import");
            if (Gui.RadioButton(EditorLocalization.Widget("No skeleton", "Button"), !reuseSkeleton && !outputs.GetValueOrDefault(typeof(SkeletonAsset))))
            { reuseSkeleton = false; outputs[typeof(SkeletonAsset)] = false; }
            RecordControl?.Invoke("fbx/options/skeleton/none");
            if (Gui.RadioButton(EditorLocalization.Widget("Use another skeleton for this model:", "Button"), reuseSkeleton))
            { reuseSkeleton = true; outputs[typeof(SkeletonAsset)] = false; }
            RecordControl?.Invoke("fbx/options/skeleton/reuse");
            using (ImGuiScopes.Disabled(!reuseSkeleton))
                if (Gui.BeginCombo("Skeleton", skeletons.FirstOrDefault(entry => entry.Id == skeleton).Name ?? ""))
                {
                    if (Gui.Selectable(EditorLocalization.Widget("None", "Button"), skeleton == null)) skeleton = null;
                    foreach (var entry in skeletons) if (Gui.Selectable(entry.Name, entry.Id == skeleton)) skeleton = entry.Id;
                    Gui.EndCombo();
                }
        }
        using (ImGuiScopes.Disabled(!outputs.Values.Any(value => value)))
            if (Gui.Button("Import")) execute(() =>
            {
                AcceptedActions++; StoreFile(); import(new Dictionary<string, AssetImportOptions>(fileOptions, StringComparer.OrdinalIgnoreCase));
                Gui.CloseCurrentPopup(); Clear(false);
            });
        RecordControl?.Invoke("fbx/options/import");
        Gui.SameLine(); var cancelPressed = Gui.Button(EditorLocalization.Widget("Cancel", "Button"));
        RecordControl?.Invoke("fbx/options/cancel");
        if (cancelPressed) { CanceledActions++; Gui.CloseCurrentPopup(); Clear(); }
    }
    internal void Clear(bool cancelled = true) { if (cancelled) OnCancelled?.Invoke(); hasDrawn = false; choices = []; files = []; fileOptions.Clear(); selected = null; outputs.Clear(); skeletons = []; import = null; requiredType = null; requestOpen = false; }
}

internal sealed record AssetImportOptionsInspection(bool Pending, Guid? ImporterId, string? ImporterName,
    Dictionary<string, bool> Outputs, bool Deduplicate, bool ReuseSkeleton, string? SkeletonId,
    bool SupportsDeduplicate, bool SupportsSkeleton, long AcceptedActions, long CanceledActions);
