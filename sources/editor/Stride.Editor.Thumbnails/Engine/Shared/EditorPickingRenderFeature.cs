// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using Stride.Core;
using Stride.Engine;
using Stride.Rendering;

namespace Stride.Engine.Design
{
    /// <summary>
    /// Performs picking.
    /// </summary>
    public class EditorPickingRenderFeature : SubRenderFeature
    {
        /// <summary>Optional host gate; WPF's existing continuous picker uses the default.</summary>
        public Func<bool> IsEnabled { get; set; }
        private ObjectPropertyKey<EditorPickingData> renderObjectInfoKey;

        private ConstantBufferOffsetReference pickingData;

        /// <inheritdoc/>
        protected override void InitializeCore()
        {
            renderObjectInfoKey = RootRenderFeature.RenderData.CreateObjectKey<EditorPickingData>();

            pickingData = ((RootEffectRenderFeature)RootRenderFeature).CreateDrawCBufferOffsetSlot(PickingShaderKeys.PickingData.Name);
        }

        /// <inheritdoc/>
        public override void Extract()
        {
            if (IsEnabled?.Invoke() == false) return;
            var modelObjectInfo = RootRenderFeature.RenderData.GetData(renderObjectInfoKey);

            foreach (var objectNodeReference in RootRenderFeature.ObjectNodeReferences)
            {
                var objectNode = RootRenderFeature.GetObjectNode(objectNodeReference);
                var renderMesh = objectNode.RenderObject as RenderMesh;
                if (renderMesh == null)
                    continue;

                int meshIndex = 0;
                var modelComponent = renderMesh.Source as ModelComponent;
                if (modelComponent == null)
                    continue;

                for (int i = 0; i < modelComponent.Model.Meshes.Count; i++)
                {
                    if (modelComponent.Model.Meshes[i] == renderMesh.Mesh)
                    {
                        meshIndex = i;
                        break;
                    }
                }

                modelObjectInfo[objectNodeReference] = new EditorPickingData(RuntimeIdHelper.ToRuntimeId(modelComponent), meshIndex, renderMesh.Mesh.MaterialIndex);
            }
        }

        /// <inheritdoc/>
        public unsafe override void Prepare(RenderDrawContext context)
        {
            if (IsEnabled?.Invoke() == false) return;
            var renderObjectInfo = RootRenderFeature.RenderData.GetData(renderObjectInfoKey);

            foreach (var renderNode in ((RootEffectRenderFeature)RootRenderFeature).RenderNodes)
            {
                var perDrawLayout = renderNode.RenderEffect.Reflection?.PerDrawLayout;
                if (perDrawLayout == null)
                    continue;

                var pickingDataOffset = perDrawLayout.GetConstantBufferOffset(this.pickingData);
                if (pickingDataOffset == -1)
                    continue;

                var renderModelObjectInfo = renderObjectInfo[renderNode.RenderObject.ObjectNode];

                var mappedCB = renderNode.Resources.ConstantBuffer.Data;
                var pickingData = (EditorPickingData*)((byte*)mappedCB + pickingDataOffset);

                *pickingData = renderModelObjectInfo;
            }
        }
    }
}

