// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core;
using Stride.Updater;
using Unsafe = System.Runtime.CompilerServices.Unsafe;

namespace Stride.Engine.Design
{
    /// <summary>
    /// Lets animation curves drive morph weights by target name: <c>[ModelComponent.Key].MorphWeights[Smile]</c>.
    /// The name is resolved to a slot once per model, so playback writes the slot array directly.
    /// </summary>
    internal class ModelMorphWeightResolver : UpdateMemberResolver
    {
        [ModuleInitializer]
        internal static void InitializeModule()
        {
            UpdateEngine.RegisterMemberResolver(new ModelMorphWeightResolver());
        }

        public override Type SupportedType => typeof(ModelComponent);

        public override UpdatableMember ResolveProperty(string memberName)
        {
            return memberName == "MorphWeights" ? new MorphWeightsAccessor() : null;
        }

        public override UpdatableMember ResolveIndexer(string indexerName)
        {
            return new MorphWeightAccessor(indexerName);
        }

        /// <summary>
        /// Enters <c>MorphWeights</c> as the component itself, so the following indexer resolves on <see cref="ModelComponent"/>.
        /// </summary>
        private class MorphWeightsAccessor : UpdatableCustomAccessor
        {
            /// <inheritdoc/>
            public override Type MemberType => typeof(ModelComponent);

            /// <inheritdoc/>
            public override void GetBlittable(IntPtr obj, IntPtr data)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc/>
            public override void SetBlittable(IntPtr obj, IntPtr data)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc/>
            public override void SetStruct(IntPtr obj, object data)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc/>
            public override IntPtr GetStructAndUnbox(IntPtr obj, object data)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc/>
            public override object GetObject(IntPtr obj)
            {
                return UpdateEngineHelper.PointerToObject<ModelComponent>(obj);
            }

            /// <inheritdoc/>
            public override void SetObject(IntPtr obj, object data)
            {
                throw new NotSupportedException();
            }
        }

        private class MorphWeightAccessor : UpdatableCustomAccessor
        {
            private readonly string targetName;
            // Slot of the target in the last model layout seen; layouts are shared by every instance of a model.
            private SlotCache slotCache = new SlotCache(null, -1);

            public MorphWeightAccessor(string targetName)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
                this.targetName = targetName;
            }

            /// <inheritdoc/>
            public override Type MemberType => typeof(float);

            /// <inheritdoc/>
            public override unsafe void GetBlittable(IntPtr obj, IntPtr data)
            {
                var modelComponent = UpdateEngineHelper.PointerToObject<ModelComponent>(obj);
                var slot = FindSlot(modelComponent);
                Unsafe.WriteUnaligned((void*)data, slot >= 0 ? modelComponent.GetMorphWeight(slot) : 0.0f);
            }

            /// <inheritdoc/>
            public override unsafe void SetBlittable(IntPtr obj, IntPtr data)
            {
                var modelComponent = UpdateEngineHelper.PointerToObject<ModelComponent>(obj);
                var slot = FindSlot(modelComponent);
                // Models without this target are skipped, so one clip can play on different models
                if (slot >= 0)
                    modelComponent.SetMorphWeight(slot, Unsafe.ReadUnaligned<float>((void*)data));
            }

            /// <inheritdoc/>
            public override void SetStruct(IntPtr obj, object data)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc/>
            public override IntPtr GetStructAndUnbox(IntPtr obj, object data)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc/>
            public override object GetObject(IntPtr obj)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc/>
            public override void SetObject(IntPtr obj, object data)
            {
                throw new NotSupportedException();
            }

            private int FindSlot(ModelComponent modelComponent)
            {
                var layout = modelComponent.MorphLayout;
                if (layout == null)
                    return -1;

                // Read once: components may be animated from several threads
                var cache = slotCache;
                if (ReferenceEquals(cache.Layout, layout))
                    return cache.Slot;

                var slot = layout.Slots.TryGetValue(targetName, out var found) ? found : -1;
                slotCache = new SlotCache(layout, slot);
                return slot;
            }

            private sealed record SlotCache(ModelMorphLayout Layout, int Slot);
        }
    }
}
