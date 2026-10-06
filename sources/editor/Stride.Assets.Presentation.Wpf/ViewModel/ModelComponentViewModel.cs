// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System.Linq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stride.Core.Assets.Editor.Quantum.NodePresenters;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Assets.Editor.Extensions;
using Stride.Core;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets;
using Stride.Core.Extensions;
using Stride.Core.Serialization;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using Stride.Assets.Models;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.ViewModels;
using Stride.Core.Collections;
using Stride.Engine;
using Stride.Rendering;
using Stride.Core.Presentation.ViewModels;

namespace Stride.Assets.Presentation.ViewModel
{
    public class ModelComponentViewModel : DispatcherViewModel
    {
        private readonly EntityViewModel entity;
        private IMemberNode modelContent;

        public ModelComponentViewModel(IViewModelServiceProvider serviceProvider, EntityViewModel entity)
            : base(serviceProvider)
        {
            this.entity = entity;
        }

        public void Initialize()
        {
            var assetNode = entity.Editor.NodeContainer.GetOrCreateNode(entity.AssetSideEntity);
            var componentNode = assetNode[nameof(Entity.Components)].Target;
            componentNode.ItemChanged += ComponentListChanged;
            RegisterModelChanged();
        }

        public override void Destroy()
        {
            var assetNode = entity.Editor.NodeContainer.GetOrCreateNode(entity.AssetSideEntity);
            var componentNode = assetNode[nameof(Entity.Components)].Target;
            componentNode.ItemChanged -= ComponentListChanged;
            UnregisterModelChanged();

            base.Destroy();
        }

        internal void UpdateNodePresenter(INodePresenter node)
        {
            if (node.Value is ModelComponent component && node.Parent?.Value is EntityComponentCollection)
            {
                // Make sure the materials get refreshed if we change the model.
                var materials = node[nameof(ModelComponent.Materials)];
                var model = node[nameof(ModelComponent.Model)];
                materials.AddDependency(model, false);
                var morphs = node[nameof(ModelComponent.Morphs)];
                morphs.AddDependency(model, true);
                morphs.IsVisible = GetReferencedModel() is ModelAsset asset && asset.ImportMorphTargets && asset.MorphTargetNames.Count != 0;
                var weightsMember = entity.Editor.NodeContainer.GetNode(component.Morphs)[nameof(ModelMorphSettings.Weights)];
                var source = ((IAssetNodePresenter)node).Factory.CreateVirtualNodePresenter(node, "MorphWeightsSource", typeof(object), null, () => component.Morphs.Weights);
                source.IsVisible = false;
                source.Commands.Clear();
                source.RegisterAssociatedNode(new NodeAccessor(weightsMember, NodeIndex.Empty));
                morphs.AddDependency(source, false);
            }

            if (node.Value is ModelMorphSettings settings)
            {
                var asset = GetReferencedModel() as ModelAsset;
                if (asset == null) return;
                // Collection edits rebuild the dictionary presenter. Keep the visible sliders
                // under a separate category so committing one weight preserves the other rows.
                var weights = node.CreateCategory("Weights", 30, ExpandRule.Once);
                var weightsNode = (IAssetObjectNode)entity.Editor.NodeContainer.GetNode(settings.Weights);
                var factory = ((IAssetNodePresenter)node).Factory;
                var weightsMember = entity.Editor.NodeContainer.GetNode(settings)[nameof(ModelMorphSettings.Weights)];
                // A whole-dictionary replacement is one Quantum edit. Rebind the sliders
                // once on replacement/undo, while individual edits keep their rows intact.
                var names = asset.MorphTargetNames.Distinct(StringComparer.Ordinal).ToArray();
                weights.Commands.Clear();
                weights.Commands.Add(new SyncAnonymousNodePresenterCommand("ResetAllMorphWeights", (_, _) => SetMorphWeights(weightsMember, names, 0f)));
                weights.Commands.Add(new SyncAnonymousNodePresenterCommand("MaxAllMorphWeights", (_, _) => SetMorphWeights(weightsMember, names, 1f)));
                weights.Commands.Add(new SyncAnonymousNodePresenterCommand("MinAllMorphWeights", (_, _) => SetMorphWeights(weightsMember, names, -1f)));
                int order = 0;
                foreach (var name in names)
                {
                    var index = new NodeIndex(name);
                    var slider = factory.CreateVirtualNodePresenter(weights, name + "___MorphWeight", typeof(float), order++,
                        () => weightsNode.Indices.Contains(index) ? weightsNode.Retrieve(index) : 0f,
                        value => SetMorphWeight(weightsNode, index, (float)value),
                        () => weightsNode.BaseNode != null,
                        () => weightsNode.Indices.Contains(index) && weightsNode.IsItemInherited(index),
                        () => weightsNode.Indices.Contains(index) && weightsNode.IsItemOverridden(index));
                    slider.DisplayName = name;
                    slider.RegisterAssociatedNode(new NodeAccessor(weightsNode, index));
                    slider.AttachedProperties.Set(NumericData.MinimumKey, -1f);
                    slider.AttachedProperties.Set(NumericData.MaximumKey, 1f);
                    slider.AttachedProperties.Set(NumericData.SmallStepKey, 0.01);
                    slider.AttachedProperties.Set(NumericData.LargeStepKey, 0.1);
                    slider.AttachedProperties.Set(NumericData.DecimalPlacesKey, 3);
                }
            }

            if (node.Value is IndexingDictionary<Material> && node.Parent?.Value is ModelComponent)
            {
                var materialsNode = (IAssetObjectNode)entity.Editor.NodeContainer.GetNode(node.Value);
                var materials = node;
                var model = GetReferencedModel();
                if (model != null)
                {
                    int i = 0;
                    foreach (var child in materials.Children.ToList())
                    {
                        child.IsVisible = false;
                    }
                    var factory = ((IAssetNodePresenter)node).Factory;
                    foreach (var material in model.Materials.ToList())
                    {
                        var modelMaterial = model.Materials.Count > i ? model.Materials[i] : null;
                        var materialName = modelMaterial?.Name ?? $"(Material {i + 1})";
                        var index = new NodeIndex(i);
                        var virtualMaterial = factory.CreateVirtualNodePresenter(node, material.Name + "___Virtual", typeof(Material), i,
                                                   () => GetMaterial(materialsNode, index),
                                                   x => SetMaterial(materialsNode, index, (Material)x),
                                                   () => materialsNode.BaseNode != null,
                                                   () => materialsNode.IsItemInherited(index),
                                                   () => materialsNode.IsItemOverridden(index));

                        // Do not put the FetchAssetCommand, we need a custom implementation for this one.
                        // Do not put the CreateNewInstanceCommand neither, otherwise it will display the "Clear reference" button which doesn't make sense here (null => disabled)
                        virtualMaterial.Commands.RemoveWhere(x => x.Name == FetchAssetCommand.CommandName || x.Name == CreateNewInstanceCommand.CommandName);

                        // Override the FetchAsset command to be able to fetch the model material when it is null in the component
                        var fetchAsset = new AnonymousNodePresenterCommand(FetchAssetCommand.CommandName, (x, param) => FetchMaterial(materialsNode, index));
                        virtualMaterial.Commands.Add(fetchAsset);
                        virtualMaterial.DisplayName = materialName;
                        virtualMaterial.RegisterAssociatedNode(new NodeAccessor(materialsNode, index));

                        var enabledNode = factory.CreateVirtualNodePresenter(virtualMaterial, "Enabled", typeof(bool), 0,
                               () => IsMaterialEnabled(materialsNode, index),
                               x => SetMaterialEnabled(materialsNode, index, (bool)x),
                               () => materialsNode.BaseNode != null,
                               () => materialsNode.IsItemInherited(index),
                               () => materialsNode.IsItemOverridden(index));
                        enabledNode.RegisterAssociatedNode(new NodeAccessor(materialsNode, index));
                        enabledNode.IsVisible = false;
                        i++;
                    }
                }
            }
        }

        private static void SetMorphWeight(IObjectNode node, NodeIndex index, float value)
        {
            if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            value = Math.Clamp(value, -1f, 1f);
            if (node.Indices.Contains(index)) node.Update(value, index);
            else node.Add(value, index);
        }

        private static void SetMorphWeights(IMemberNode member, string[] names, float value)
        {
            var current = (Dictionary<string, float>)member.Retrieve();
            if (names.All(name => (current.TryGetValue(name, out var weight) ? weight : 0f) == value)) return;
            // Preserve existing item IDs for asset serialization and prefab inheritance.
            var replacement = new ModelMorphSettings { Weights = AssetCloner.Clone(current) };
            replacement.SetAllWeights(names, value);
            member.Update(replacement.Weights);
        }

        private Task FetchMaterial(IObjectNode materialsNode, NodeIndex index)
        {
            var material = GetMaterial(materialsNode, index);
            return FetchAssetCommand.Fetch(entity.Editor.Session, material);
        }

        private static void SetMaterial(IObjectNode materialNode, NodeIndex index, Material value)
        {
            if (materialNode.Indices.Contains(index))
            {
                materialNode.Update(value, index);
            }
            else
            {
                materialNode.Add(value, index);
            }
        }

        private object GetMaterial(IObjectNode materialNode, NodeIndex index)
        {
            if (materialNode.Indices.Contains(index))
            {
                return materialNode.Retrieve(index);
            }

            var model = GetReferencedModel();
            if (model == null)
                return null;

            // During specific operations such as changes of the referenced model, this getter can be used while we're not currently in sync with the collection
            // of the model. In this case, return null.
            if (model.Materials.Count <= index.Int)
                return null;

            return model?.Materials[index.Int].MaterialInstance.Material;
        }

        private void SetMaterialEnabled(IObjectNode materialNode, NodeIndex index, bool value)
        {
            if (value)
            {
                var material = GetMaterial(materialNode, index);
                materialNode.Add(material, index);
            }
            else
            {
                var material = materialNode.Retrieve(index);
                materialNode.Remove(material, index);
            }
        }

        private static object IsMaterialEnabled(IObjectNode materialNode, NodeIndex index)
        {
            return materialNode.Indices.Contains(index);
        }

        private static void ClearMaterialList(IObjectNode materials)
        {
            var indices = materials.Indices.ToList();
            foreach (var index in indices)
            {
                var item = materials.Retrieve(index);
                materials.Remove(item, index);
            }
        }

        private IObjectNode GetMaterialsNode()
        {
            var modelComponent = entity.AssetSideEntity.Get<ModelComponent>();
            return modelComponent != null ? entity.Editor.NodeContainer.GetNode(modelComponent)[nameof(ModelComponent.Materials)].Target : null;
        }

        private IModelAsset GetReferencedModel()
        {
            var modelReference = entity.AssetSideEntity.Get<ModelComponent>()?.Model;
            if (modelReference == null)
                return null;

            var modelUrl = AttachedReferenceManager.GetUrl(modelReference);
            return entity.Editor.Session.AllAssets.FirstOrDefault(x => x.Url == modelUrl)?.Asset as IModelAsset;
        }

        private void ComponentListChanged(object sender, ItemChangeEventArgs e)
        {
            if (e.ChangeType == ContentChangeType.CollectionAdd)
            {
                if (e.NewValue is ModelComponent)
                {
                    RegisterModelChanged();
                }
            }
            if (e.ChangeType == ContentChangeType.CollectionRemove)
            {
                if (e.OldValue is ModelComponent)
                {
                    UnregisterModelChanged();
                }
            }
        }

        private void RegisterModelChanged()
        {
            UnregisterModelChanged();
            var modelComponent = entity.AssetSideEntity.Get<ModelComponent>();
            if (modelComponent != null)
            {
                var modelNode = entity.Editor.NodeContainer.GetNode(modelComponent);
                modelContent = modelNode[nameof(ModelComponent.Model)];
                modelContent.ValueChanging += ModelChanging;
            }
        }

        private void UnregisterModelChanged()
        {
            if (modelContent != null)
            {
                modelContent.ValueChanging -= ModelChanging;
                modelContent = null;
            }
        }

        private void ModelChanging(object sender, MemberNodeChangeEventArgs e)
        {
            if (e.NewValue != e.OldValue && !entity.Editor.UndoRedoService.UndoRedoInProgress)
            {
                var materials = GetMaterialsNode();
                ClearMaterialList(materials);
                var component = entity.AssetSideEntity.Get<ModelComponent>();
                var weights = entity.Editor.NodeContainer.GetNode(component.Morphs.Weights);
                foreach (var index in weights.Indices.ToList()) weights.Remove(weights.Retrieve(index), index);
            }
        }
    }
}
