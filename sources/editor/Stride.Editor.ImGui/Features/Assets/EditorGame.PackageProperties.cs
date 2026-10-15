using Hexa.NET.ImGui;
using Stride.Assets;
using Stride.Core.Assets;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Engine.Design;
using System.Globalization;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Package? inspectedPackage;
    private EntityHierarchyDocument? packageInspectorScene;
    private long packageInspectorAssetSelection, packageInspectorSceneSelection;
    private bool resetPackageInspectorScroll;
    private ImFontPtr packagePropertiesBoldFont;
    private static readonly object builtInPackageSettingsProvider = new();
    private static bool builtInPackageSettingsRegistered;
    private readonly Dictionary<Stride.Core.Settings.SettingsKey, PackageSettingAccessor> packageSettingAccessors = [];
    private IReadOnlyList<PackageSettingRegistration> packageSettingRegistrations = [];
    private long packageSettingsRegistryNextCheck;
    private long packageSettingsRegistryVersion = -1;
    private bool? packageSettingsExecutable;
    private string? failedPackageProjectPath;
    private long nextPackageProjectAttempt;
    private unsafe void InitializePackagePropertiesFont()
    {
        var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeuib.ttf");
        if (File.Exists(path)) packagePropertiesBoldFont = Gui.GetIO().Fonts.AddFontFromFileTTF(path, 17);
    }
    private void ClearPackagePropertiesState()
    {
        ClearPackagePropertyStrings();
        ClearPackageDependencyRemovalCheckState();
        ClearPackageDependencyRestoreState();
        ClearPackageDependencyState();
        inspectedPackage = null; packageInspectorScene = null;
        packageSettingAccessors.Clear();
        ClearPackageSettingTrees();
        packageDictionaryAddViews.Clear();
        packageDateTimeViews.Clear();
        packageSettingRegistrations = []; packageSettingsExecutable = null; packageSettingsRegistryNextCheck = 0;
        packageSettingsRegistryVersion = -1;
        failedPackageProjectPath = null; nextPackageProjectAttempt = 0;
    }
    private unsafe bool PushPackagePropertiesBoldFont(bool enabled)
    {
        if (!enabled || packagePropertiesBoldFont.Handle == null) return false;
        Gui.PushFont(packagePropertiesBoldFont, 17); return true;
    }
    private IReadOnlyList<PackageSettingRegistration> GetPackageSettingRegistrations(bool executable)
    {
        if (!builtInPackageSettingsRegistered)
        {
            PackageSettingsRegistry.Register(builtInPackageSettingsProvider, () =>
            [new(GameUserSettings.Effect.EffectCompilation, PackageSettingsTarget.Executable),
                new(GameUserSettings.Effect.RecordUsedEffects, PackageSettingsTarget.Executable)]);
            builtInPackageSettingsRegistered = true;
        }
        var now = Environment.TickCount64;
        var registryVersion = PackageSettingsRegistry.Version;
        if (packageSettingsExecutable == executable && packageSettingsRegistryVersion == registryVersion && now < packageSettingsRegistryNextCheck) return packageSettingRegistrations;
        packageSettingsExecutable = executable; packageSettingsRegistryNextCheck = now + 1000;
        packageSettingsRegistryVersion = registryVersion;
        packageSettingRegistrations = PackageSettingsRegistry.GetRegistrations(executable);
        var active = packageSettingRegistrations.Select(registration => registration.Key).ToHashSet();
        foreach (var key in packageSettingAccessors.Keys.Where(key => !active.Contains(key)).ToArray()) packageSettingAccessors.Remove(key);
        foreach (var key in packageSettingTrees.Keys.Where(key => !active.Contains(key.Item2)).ToArray()) RemovePackageSettingTree(key);
        return packageSettingRegistrations;
    }
    private void RequestPackageProperties(Package package)
    {
        inspectedPackage = package; packageInspectorScene = document;
        packageInspectorAssetSelection = assetBrowser.SelectionVersion;
        packageInspectorSceneSelection = document?.SelectionVersion ?? 0;
        showInspector = true; resetPackageInspectorScroll = true;
    }
    private bool TryDrawPackageInspector()
    {
        if (inspectedPackage == null) return false;
        if (workspace == null || assetBrowser.SelectionVersion != packageInspectorAssetSelection
            || !ReferenceEquals(document, packageInspectorScene) || (document?.SelectionVersion ?? 0) != packageInspectorSceneSelection
            || !workspace.Session.LocalPackages.Contains(inspectedPackage) && !workspace.AssetItems.Any(item => ReferenceEquals(item.Package, inspectedPackage)))
        { inspectedPackage = null; packageInspectorScene = null; return false; }
        if (resetPackageInspectorScroll) { Gui.SetScrollY(0); resetPackageInspectorScroll = false; }
        DrawPackageProperties(inspectedPackage, SessionAuthoringHistory);
        return true;
    }
    private void DrawPackageProperties(Package package, EditorSessionHistory? history)
    {
        Gui.PushID(package.FullPath.ToString());
        var settings = GetPackageSettingRegistrations(package.Container is SolutionProject { Type: ProjectType.Executable });
        if (settings.Count > 0 && Gui.CollapsingHeader("User settings", ImGuiTreeNodeFlags.DefaultOpen))
        {
            using var referenceOwners = BeginContentReferenceOwnerScope(history == null ? [] : [GetPackageSettingsDocument(package, history)]);
            foreach (var registration in settings) DrawRegisteredPackageSetting(package, history, registration);
        }
        if (history != null && package.Container is SolutionProject selectedProject && Gui.CollapsingHeader("Build", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var path = selectedProject.FullPath.ToOSPath();
            ProjectBuildSettingsDocument? build = null;
            if (failedPackageProjectPath != path || Environment.TickCount64 >= nextPackageProjectAttempt)
            {
                Execute(() => build = GetProjectBuildSettingsDocument(selectedProject, history));
                if (build == null) { failedPackageProjectPath = path; nextPackageProjectAttempt = Environment.TickCount64 + 1000; }
                else failedPackageProjectPath = null;
            }
            if (build == null) { Gui.PopID(); return; }
            Execute(build.RefreshIfClean);
            Gui.BeginDisabled(!build.CanSave);
            if (selectedProject.Type == ProjectType.Executable && selectedProject.Platform == Stride.Core.PlatformType.Windows)
            {
                var bold = PushPackagePropertiesBoldFont(build.Raw(ProjectBuildSettingsDocument.GraphicsProperty) != null);
                if (Gui.BeginCombo("Graphics API", build.GraphicsApi.ToString()))
                {
                    foreach (var candidate in Enum.GetValues<ProjectGraphicsApi>())
                        if (Gui.Selectable(candidate.ToString(), candidate == build.GraphicsApi))
                        { var selected = candidate; Execute(() => build.SetGraphics(selected)); }
                    Gui.EndCombo();
                }
                if (bold) Gui.PopFont();
                if (Gui.BeginPopupContextItem("Graphics API actions"))
                {
                    if (DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), "", false, build.CanSave && build.Raw(ProjectBuildSettingsDocument.GraphicsProperty) != null))
                        Execute(() => build.SetGraphics(ProjectGraphicsApi.Default));
                    Gui.EndPopup();
                }
            }
            var contains = build.ContainsAssetTypes;
            var containsBold = PushPackagePropertiesBoldFont(build.Raw(SolutionProject.ContainsAssetTypesProperty) != null);
            if (Gui.Checkbox("Contains asset types", ref contains)) Execute(() => build.SetContains(contains));
            if (containsBold) Gui.PopFont();
            if (Gui.BeginPopupContextItem("Contains asset types actions"))
            {
                if (DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), "", false, build.CanSave && build.Raw(SolutionProject.ContainsAssetTypesProperty) != null))
                    Execute(() => build.SetContains(null));
                Gui.EndPopup();
            }
            Gui.EndDisabled();
        }
        Gui.PopID();
    }
    private static PackageSettingsDocument GetPackageSettingsDocument(Package package, EditorSessionHistory history) =>
        history.Registry.GetOrCreate(package.FullPath.ToOSPath() + ".user", null, () => new PackageSettingsDocument(package, history)).Document;
    private static ProjectBuildSettingsDocument GetProjectBuildSettingsDocument(SolutionProject project, EditorSessionHistory history) =>
        history.Registry.GetOrCreate(project.FullPath.ToOSPath(), null, () => new ProjectBuildSettingsDocument(project, history)).Document;
    private void DrawRegisteredPackageSetting(Package package, EditorSessionHistory? history, PackageSettingRegistration registration)
    {
        var key = registration.Key;
        if (!packageSettingAccessors.TryGetValue(key, out var accessor)) packageSettingAccessors.Add(key, accessor = PackageSettingAccessor.Create(key));
        var value = accessor.Get(package); var type = key.Type;
        var tree = GetPackageSettingTree(package, accessor);
        var presenter = tree.Root is { } root ? tree.Presentation?.Find(root) : null;
        if (presenter?.IsVisible == false) return;
        var label = string.IsNullOrEmpty(presenter?.DisplayName) ? key.DisplayName.GetFileName() : presenter.DisplayName;
        void Set(object? updated)
        {
            var range = tree.Root is { } node ? tree.Presentation?.NumericRange(node, type) : null;
            accessor.Set(GetPackageSettingsDocument(package, history!), range?.Normalize(updated) ?? updated);
        }
        Gui.PushID(key.Name.ToString());
        if (TryDrawPackageSettingValue(package, accessor, type, label, value, "", false, presenter?.IsReadOnly != true,
            Set, presenter)) { }
        else if (IsPackageSettingScalar(type)) DrawPackageSettingScalar(package, accessor, type, label, value, "",
            Set, presenter, presenter?.IsReadOnly != true);
        else DrawPackageSettingTree(package, accessor, label);
        if (IsPackageSettingScalar(type) || (Nullable.GetUnderlyingType(type) ?? type) == typeof(Stride.Core.Mathematics.AngleSingle) || IsPackageVectorType(Nullable.GetUnderlyingType(type) ?? type) || IsPackageColorType(Nullable.GetUnderlyingType(type) ?? type) || IsPackageRectangleType(Nullable.GetUnderlyingType(type) ?? type) || (Nullable.GetUnderlyingType(type) ?? type) == typeof(Stride.Core.Mathematics.Matrix) || (Nullable.GetUnderlyingType(type) ?? type) == typeof(Stride.Core.Mathematics.Quaternion) || ContentAssetReference.Supports(type) || SessionAuthoringHistory?.Extensions?.HasPrimitiveEditor(type) == true)
            DrawPackageSettingCommands(package, accessor, presenter);
        Gui.PopID();
        DispatchPendingPackageSettingCommand();
    }
    private static bool IsPackageSettingScalar(Type type) => type.IsEnum || type == typeof(string) || type == typeof(bool)
        || Type.GetTypeCode(type) is TypeCode.Char or TypeCode.SByte or TypeCode.Byte or TypeCode.Int16 or TypeCode.UInt16
            or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal or TypeCode.DateTime
        || System.ComponentModel.TypeDescriptor.GetConverter(type).CanConvertFrom(typeof(string));
    private bool PackageSettingsEditable(Package package) => !package.IsReadOnly && package.State >= PackageState.AssetsReady
        && SessionAuthoringHistory != null && !IsAuthoringPipelineUpdating;
    private bool UsesPackageTextTemplate(Type type) => SessionAuthoringHistory?.Extensions?.HasPrimitiveEditor(type) != true
        && IsPackageSettingScalar(Nullable.GetUnderlyingType(type) ?? type)
        && type != typeof(bool) && !type.IsEnum;
    private bool UsesPackageSelectableTemplate(Type type) => UsesPackageTextTemplate(type)
        || SessionAuthoringHistory?.Extensions?.HasPrimitiveEditor(type) != true
        && (IsPackageVectorType(Nullable.GetUnderlyingType(type) ?? type)
            || IsPackageColorType(Nullable.GetUnderlyingType(type) ?? type) || IsPackageRectangleType(Nullable.GetUnderlyingType(type) ?? type)
            || (Nullable.GetUnderlyingType(type) ?? type) == typeof(Stride.Core.Mathematics.AngleSingle)
            || (Nullable.GetUnderlyingType(type) ?? type) == typeof(Stride.Core.Mathematics.Matrix) || (Nullable.GetUnderlyingType(type) ?? type) == typeof(Stride.Core.Mathematics.Quaternion));
    private void DrawPackageSettingScalar(Package package, PackageSettingAccessor accessor, Type type, string label, object? value, string path, Action<object?> set, INodePresenter? presenter = null, bool canEdit = true)
    {
        var key = accessor.Key;
        if (type == typeof(bool))
        {
            Gui.BeginDisabled(!canEdit || !PackageSettingsEditable(package) || PackagePresenterReadOnly(presenter));
            var boolean = (bool)value!;
            if (Gui.Checkbox(label, ref boolean)) Execute(() => set(boolean));
            Gui.EndDisabled();
        }
        else if (type.IsEnum)
            DrawPackageEnumValue(package, accessor, presenter?.Type ?? type, label, value, set, presenter, canEdit);
        else
        {
            var feedback = Gui.GetID(label);
            PropertyNumericRange? Range() => packageSettingTrees.GetValueOrDefault((package, key))?.Presentation?.NumericRange(presenter, type);
            object? Read() => presenter == null ? accessor.Get(package) : presenter.Value;
            var declared = presenter?.Type ?? (path.Length == 0 ? key.Type : type);
            string? sliderText = null;
            var sliderActive = false;
            var sliderCommit = false;
            if (SessionAuthoringHistory != null && NativeRangedNumericControl.Matches(declared, Range()))
            {
                using (ImGuiScopes.Disabled(!canEdit || !PackageSettingsEditable(package) || PackagePresenterReadOnly(presenter)))
                {
                    sliderCommit = NativeRangedNumericControl.Draw((object?)presenter ?? GetPackageSettingsDocument(package, SessionAuthoringHistory!),
                        type, Read(), false, [Range()], UiScale(1), out var display, out _);
                    sliderActive = Gui.IsItemActive();
                    sliderText = Convert.ToString(display, CultureInfo.InvariantCulture) ?? "";
                }
            }
            DrawPackagePropertyString(package, accessor, label, presenter, canEdit,
                Read,
                selected =>
                {
                    var converted = PropertyConversionFeedback.Convert(feedback, () => IsStockPackageNumeric(type) ? NativeNumericText.Parse(selected, type, Range()) : type == typeof(string) ? selected : System.ComponentModel.TypeDescriptor.GetConverter(type).CanConvertFrom(typeof(string))
                        ? System.ComponentModel.TypeDescriptor.GetConverter(type).ConvertFromInvariantString(selected) : Convert.ChangeType(selected, type, CultureInfo.InvariantCulture));
                    var normalized = AssetSettingNumericMetadata.Supports(type) ? Range()?.Normalize(converted) ?? converted : converted;
                    if (!Equals(Read(), normalized)) set(normalized);
                }, format: value => IsStockPackageNumeric(type) ? NativeNumericText.Format(value, type, Range())
                    : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
                step: text => StepPackageNumeric(type, declared, Read(), Range(), ref text) ? text : null,
                externalText: sliderText, externalActive: sliderActive, externalCommit: sliderCommit);
            PropertyConversionFeedback.Draw(feedback);
        }
    }
}
