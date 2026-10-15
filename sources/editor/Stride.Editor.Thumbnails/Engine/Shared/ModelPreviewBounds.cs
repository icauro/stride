// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using Stride.Core.Mathematics;
using Stride.Rendering;
using System;
using System.Collections.Generic;

namespace Stride.Engine;

/// <summary>Model framing bounds shared by asset preview hosts, including bind-pose skinning.</summary>
public static class ModelPreviewBounds
{
    public static BoundingSphere Calculate(Entity entity, Func<Model, IEnumerable<Mesh>> meshSelector = null)
    {
        entity.Transform.UpdateWorldMatrix();
        var worldMatrix = entity.Transform.WorldMatrix;
        var boundingSphere = BoundingSphere.Empty;
        var modelComponent = entity.Get<ModelComponent>();
        if (modelComponent?.Model == null) return new BoundingSphere(worldMatrix.TranslationVector, 0);
        var hierarchy = modelComponent.Skeleton;
        var nodeTransforms = new Matrix[hierarchy.Nodes.Length];

        // Calculate node transforms here, since there might not be a ModelProcessor running
        for (int i = 0; i < nodeTransforms.Length; i++)
        {
            if (hierarchy.Nodes[i].ParentIndex == -1)
            {
                nodeTransforms[i] = worldMatrix;
            }
            else
            {
                Matrix localMatrix;

                Matrix.Transformation(
                    ref hierarchy.Nodes[i].Transform.Scale,
                    ref hierarchy.Nodes[i].Transform.Rotation,
                    ref hierarchy.Nodes[i].Transform.Position, out localMatrix);

                Matrix.Multiply(ref localMatrix, ref nodeTransforms[hierarchy.Nodes[i].ParentIndex], out nodeTransforms[i]);
            }
        }

        // calculate the bounding sphere
        var boundingBox = BoundingBoxExt.Empty;
            
        var meshes = modelComponent.Model.Meshes;
        var filteredMeshes = meshSelector == null ? meshes : meshSelector(modelComponent.Model);

        // Calculate skinned bounding boxes.
        // TODO: Cloned from ModelSkinningUpdater. Consolidate.
        foreach (var mesh in filteredMeshes)
        {
            var skinning = mesh.Skinning;

            if (skinning == null)
            {
                // For unskinned meshes, use the original bounding box
                var boundingBoxExt = (BoundingBoxExt)mesh.BoundingBox;
                boundingBoxExt.Transform(nodeTransforms[mesh.NodeIndex]);
                BoundingBoxExt.Merge(ref boundingBox, ref boundingBoxExt, out boundingBox);
            }
            else
            {
                var bones = skinning.Bones;
                var bindPoseBoundingBox = new BoundingBoxExt(mesh.BoundingBox);

                for (var index = 0; index < bones.Length; index++)
                {
                    var nodeIndex = bones[index].NodeIndex;
                    Matrix boneMatrix;

                    // Compute bone matrix
                    Matrix.Multiply(ref bones[index].LinkToMeshMatrix, ref nodeTransforms[nodeIndex], out boneMatrix);

                    // Fast AABB transform: http://zeuxcg.org/2010/10/17/aabb-from-obb-with-component-wise-abs/
                    // Compute transformed AABB (by world)
                    var boundingBoxExt = bindPoseBoundingBox;
                    boundingBoxExt.Transform(boneMatrix);
                    BoundingBoxExt.Merge(ref boundingBox, ref boundingBoxExt, out boundingBox);
                }
            }
        }
        var halfSize = boundingBox.Extent;
        var maxHalfSize = Math.Max(halfSize.X, Math.Max(halfSize.Y, halfSize.Z));
        boundingSphere = BoundingSphere.Merge(boundingSphere, new BoundingSphere(boundingBox.Center, maxHalfSize));
        return boundingSphere;
    }
}

