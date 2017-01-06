﻿// Copyright (c) 2014 Silicon Studio Corp. (http://siliconstudio.co.jp)
// This file is distributed under GPL v3. See LICENSE.md for details.

using System;
using System.Collections.Generic;
using System.Linq;
using SiliconStudio.Assets.Analysis;
using SiliconStudio.Core.Diagnostics;
using SiliconStudio.Core.Reflection;
using SiliconStudio.Core.Serialization;
using SiliconStudio.Core.Yaml;
using SiliconStudio.Quantum;
using SiliconStudio.Quantum.Contents;
using SiliconStudio.Quantum.References;

namespace SiliconStudio.Assets.Quantum
{
    [AssetPropertyGraph(typeof(Asset))]
    public class AssetPropertyGraph : IDisposable
    {
        public struct NodeOverride
        {
            public NodeOverride(AssetNode overriddenNode, Index overriddenIndex, OverrideTarget target)
            {
                Node = overriddenNode;
                Index = overriddenIndex;
                Target = target;
            }
            public readonly AssetNode Node;
            public readonly Index Index;
            public readonly OverrideTarget Target;
        }

        private readonly Dictionary<IContentNode, OverrideType> previousOverrides = new Dictionary<IContentNode, OverrideType>();
        private readonly Dictionary<IContentNode, ItemId> removedItemIds = new Dictionary<IContentNode, ItemId>();

        protected readonly Asset Asset;
        private readonly AssetToBaseNodeLinker baseLinker;
        private readonly GraphNodeChangeListener nodeListener;
        private AssetPropertyGraph baseGraph;
        private readonly Dictionary<AssetNode, EventHandler<ContentChangeEventArgs>> baseLinkedNodes = new Dictionary<AssetNode, EventHandler<ContentChangeEventArgs>>();

        public AssetPropertyGraph(AssetPropertyGraphContainer container, AssetItem assetItem, ILogger logger)
        {
            if (container == null) throw new ArgumentNullException(nameof(container));
            if (assetItem == null) throw new ArgumentNullException(nameof(assetItem));
            Container = container;
            AssetCollectionItemIdHelper.GenerateMissingItemIds(assetItem.Asset);
            CollectionItemIdsAnalysis.FixupItemIds(assetItem, logger);
            Asset = assetItem.Asset;
            RootNode = (AssetNode)Container.NodeContainer.GetOrCreateNode(assetItem.Asset);
            ApplyOverrides(RootNode, assetItem.Overrides);
            nodeListener = new GraphNodeChangeListener(RootNode, ShouldListenToTargetNode);
            nodeListener.Changing += AssetContentChanging;
            nodeListener.Changed += AssetContentChanged;

            baseLinker = new AssetToBaseNodeLinker(this) { LinkAction = LinkBaseNode };
        }

        public void Dispose()
        {
            nodeListener.Dispose();
        }

        public AssetNode RootNode { get; }

        public AssetPropertyGraphContainer Container { get; }

        /// <summary>
        /// Gets or sets whether a property is currently being updated from a change in the base of this asset.
        /// </summary>
        public bool UpdatingPropertyFromBase { get; private set; }

        /// <summary>
        /// Raised before one of the node referenced by the related root node changes and before the <see cref="Changing"/> event is raised.
        /// </summary>
        public event EventHandler<GraphContentChangeEventArgs> PrepareChange { add { nodeListener.PrepareChange += value; } remove { nodeListener.PrepareChange -= value; } }

        /// <summary>
        /// Raised after one of the node referenced by the related root node has changed and after the <see cref="Changed"/> event is raised.
        /// </summary>
        public event EventHandler<GraphContentChangeEventArgs> FinalizeChange { add { nodeListener.FinalizeChange += value; } remove { nodeListener.FinalizeChange -= value; } }

        /// <summary>
        /// Raised after one of the node referenced by the related root node has changed.
        /// </summary>
        public event EventHandler<GraphContentChangeEventArgs> Changing { add { nodeListener.Changing += value; } remove { nodeListener.Changing -= value; } }

        /// <summary>
        /// Raised after one of the node referenced by the related root node has changed.
        /// </summary>
        /// <remarks>In addition to the usual <see cref="ContentChangeEventArgs"/> generated by <see cref="IContent"/> this event also gives information about override changes.</remarks>
        /// <seealso cref="AssetContentChangeEventArgs"/>.
        public event EventHandler<AssetContentChangeEventArgs> Changed;

        /// <summary>
        /// Raised when a base content has changed, after updating the related content of this graph.
        /// </summary>
        public Action<ContentChangeEventArgs, IContent> BaseContentChanged;

        public void RefreshBase(AssetPropertyGraph baseAssetGraph)
        {
            // Unlink previously linked nodes
            foreach (var linkedNode in baseLinkedNodes.Where(x => x.Value != null))
            {
                linkedNode.Key.BaseContent.Changed -= linkedNode.Value;
            }
            baseLinkedNodes.Clear();

            baseGraph = baseAssetGraph;

            // Link nodes to the new base.
            // Note: in case of composition (prefabs, etc.), even if baseAssetGraph is null, each part (entities, etc.) will discover
            // its own base by itself via the FindTarget method.
            LinkToBase(RootNode, baseAssetGraph?.RootNode);
        }

        public void ReconcileWithBase()
        {
            ReconcileWithBase(RootNode);
        }

        public void ReconcileWithBase(AssetNode rootNode)
        {
            var visitor = CreateReconcilierVisitor();
            visitor.Visiting += (node, path) => ReconcileWithBaseNode((AssetNode)node);
            visitor.Visit(rootNode);
        }

        // TODO: turn protected
        public virtual bool ShouldListenToTargetNode(MemberContent member, IGraphNode targetNode)
        {
            return true;
        }

        /// <summary>
        /// Creates an instance of <see cref="GraphVisitorBase"/> that is suited to reconcile properties with the base.
        /// </summary>
        /// <returns>A new instance of <see cref="GraphVisitorBase"/> for reconciliation.</returns>
        public virtual GraphVisitorBase CreateReconcilierVisitor()
        {
            return new GraphVisitorBase();
        }

        public virtual IGraphNode FindTarget(IGraphNode sourceNode, IGraphNode target)
        {
            return target;
        }

        public void PrepareForSave(ILogger logger, AssetItem assetItem)
        {
            if (assetItem.Asset != Asset) throw new ArgumentException($@"The given {nameof(AssetItem)} does not match the asset associated with this instance", nameof(assetItem));
            AssetCollectionItemIdHelper.GenerateMissingItemIds(assetItem.Asset);
            CollectionItemIdsAnalysis.FixupItemIds(assetItem, logger);
            assetItem.Overrides = GenerateOverridesForSerialization(RootNode);
        }

        public static Dictionary<YamlAssetPath, OverrideType> GenerateOverridesForSerialization(IGraphNode rootNode)
        {
            if (rootNode == null) throw new ArgumentNullException(nameof(rootNode));

            var visitor = new OverrideTypePathGenerator();
            visitor.Visit(rootNode);
            return visitor.Result;
        }

        public static void ApplyOverrides(AssetNode rootNode, IDictionary<YamlAssetPath, OverrideType> overrides)
        {
            if (rootNode == null) throw new ArgumentNullException(nameof(rootNode));

            if (overrides == null)
                return;

            foreach (var overrideInfo in overrides)
            {
                Index index;
                bool overrideOnKey;
                var node = rootNode.ResolveObjectPath(overrideInfo.Key, out index, out overrideOnKey);
                // The node is unreachable, skip this override.
                if (node == null)
                    continue;

                if (index == Index.Empty)
                {
                    node.SetContentOverride(overrideInfo.Value);
                }
                else if (!overrideOnKey)
                {
                    node.SetItemOverride(overrideInfo.Value, index);
                }
                else
                {
                    node.SetKeyOverride(overrideInfo.Value, index);
                }
            }
        }

        public List<NodeOverride> ClearAllOverrides()
        {
            // Unregister handlers - must be done first!
            foreach (var linkedNode in baseLinkedNodes.Where(x => x.Value != null))
            {
                linkedNode.Key.BaseContent.Changed -= linkedNode.Value;
            }
            baseLinkedNodes.Clear();

            var clearedOverrides = new List<NodeOverride>();
            // Clear override and base from node
            if (RootNode != null)
            {
                var visitor = new GraphVisitorBase { SkipRootNode = true };
                visitor.Visiting += (node, path) =>
                {
                    var assetNode = (AssetNode)node;
                    if (assetNode.IsContentOverridden())
                    {
                        assetNode.OverrideContent(false);
                        clearedOverrides.Add(new NodeOverride(assetNode, Index.Empty, OverrideTarget.Content));
                    }
                    foreach (var index in assetNode.GetOverriddenItemIndices())
                    {
                        assetNode.OverrideItem(false, index);
                        clearedOverrides.Add(new NodeOverride(assetNode, index, OverrideTarget.Item));
                    }
                    foreach (var index in assetNode.GetOverriddenKeyIndices())
                    {
                        assetNode.OverrideKey(false, index);
                        clearedOverrides.Add(new NodeOverride(assetNode, index, OverrideTarget.Key));
                    }
                };
                visitor.Visit(RootNode);
            }

            return clearedOverrides;
        }

        public void RestoreOverrides(List<NodeOverride> overridesToRestore, AssetPropertyGraph archetypeBase)
        {
            foreach (var clearedOverride in overridesToRestore)
            {
                // TODO: this will need improvement when adding support for Seal
                switch (clearedOverride.Target)
                {
                    case OverrideTarget.Content:
                        clearedOverride.Node.OverrideContent(true);
                        break;
                    case OverrideTarget.Item:
                        clearedOverride.Node.OverrideItem(true, clearedOverride.Index);
                        break;
                    case OverrideTarget.Key:
                        clearedOverride.Node.OverrideKey(true, clearedOverride.Index);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }


        // TODO: turn private
        public void LinkToBase(AssetNode sourceRootNode, AssetNode targetRootNode)
        {
            baseLinker.ShouldVisit = (member, node) => (node == sourceRootNode || !baseLinkedNodes.ContainsKey((AssetNode)node)) && ShouldListenToTargetNode(member, node);
            baseLinker.LinkGraph(sourceRootNode, targetRootNode);
        }

        // TODO: this method is should be called in every scenario of ReconcileWithBase, it is not the case yet.
        protected virtual bool CanUpdate(AssetNode node, ContentChangeType changeType, Index index, object value)
        {
            return true;
        }

        protected internal virtual object CloneValueFromBase(object value, AssetNode node)
        {
            return AssetNode.CloneFromBase(value);
        }

        private void LinkBaseNode(IGraphNode currentNode, IGraphNode baseNode)
        {
            var assetNode = (AssetNode)currentNode;
            assetNode.PropertyGraph = this;
            assetNode.SetBase(baseNode?.Content);
            if (!baseLinkedNodes.ContainsKey(assetNode))
            {
                EventHandler<ContentChangeEventArgs> action = null;
                if (baseNode != null)
                {
                    action = (s, e) => OnBaseContentChanged(e, currentNode.Content);
                    assetNode.BaseContent.Changed += action;
                }
                baseLinkedNodes.Add(assetNode, action);
            }
        }

        private void AssetContentChanging(object sender, ContentChangeEventArgs e)
        {
            var overrideValue = OverrideType.Base;
            var node = (AssetNode)e.Content.OwnerNode;
            if (e.ChangeType == ContentChangeType.ValueChange || e.ChangeType == ContentChangeType.CollectionRemove)
            {
                // For value change and remove, we store the current override state.
                if (e.Index == Index.Empty)
                {
                    overrideValue = node.GetContentOverride();
                }
                else if (!node.IsNonIdentifiableCollectionContent)
                {
                    overrideValue = node.GetItemOverride(e.Index);
                }
            }
            if (e.ChangeType == ContentChangeType.CollectionRemove)
            {
                // For remove, we also collect the id of the item that will be removed, so we can pass it to the Changed event.
                var itemId = ItemId.Empty;
                CollectionItemIdentifiers ids;
                if (CollectionItemIdHelper.TryGetCollectionItemIds(e.Content.Retrieve(), out ids))
                {
                    ids.TryGet(e.Index.Value, out itemId);
                }
                removedItemIds[e.Content.OwnerNode] = itemId;
            }
            if (e.ChangeType == ContentChangeType.CollectionAdd && !node.IsNonIdentifiableCollectionContent)
            {
                // If the change is an add, we set the previous override as New so the Undo will try to remove the item instead of resetting to the base value
                previousOverrides[e.Content.OwnerNode] = OverrideType.New;
            }
            previousOverrides[e.Content.OwnerNode] = overrideValue;
        }

        private void AssetContentChanged(object sender, ContentChangeEventArgs e)
        {
            var previousOverride = previousOverrides[e.Content.OwnerNode];
            previousOverrides.Remove(e.Content.OwnerNode);

            var itemId = ItemId.Empty;
            var overrideValue = OverrideType.Base;
            var node = (AssetNode)e.Content.OwnerNode;
            if (e.ChangeType == ContentChangeType.ValueChange || e.ChangeType == ContentChangeType.CollectionAdd)
            {
                if (e.Index == Index.Empty)
                {
                    // No index, we're changing an object that is not in a collection, let's just retrieve it's override status.
                    overrideValue = node.GetContentOverride();
                }
                else
                {
                    // We're changing an item of a collection. If the collection has identifiable items, retrieve the override status of the item.
                    if (!node.IsNonIdentifiableCollectionContent)
                    {
                        overrideValue = node.GetItemOverride(e.Index);
                    }
                    // Also retrieve the id of the modified item (this should fail only if the collection doesn't have identifiable items)
                    CollectionItemIdentifiers ids;
                    if (CollectionItemIdHelper.TryGetCollectionItemIds(e.Content.Retrieve(), out ids))
                    {
                        ids.TryGet(e.Index.Value, out itemId);
                    }
                }
            }
            else
            {
                // When deleting we are always overriding (unless there is no base)
                overrideValue = !((AssetNode)node.BaseContent?.OwnerNode)?.contentUpdating == true ? OverrideType.New : OverrideType.Base;
                itemId = removedItemIds[e.Content.OwnerNode];
                removedItemIds.Remove(e.Content.OwnerNode);
            }

            Changed?.Invoke(sender, new AssetContentChangeEventArgs(e, previousOverride, overrideValue, itemId));
        }

        private void OnBaseContentChanged(ContentChangeEventArgs e, IContent assetContent)
        {
            // Ignore base change if propagation is disabled.
            if (!Container.PropagateChangesFromBase)
                return;

            UpdatingPropertyFromBase = true;
            // TODO: we want to refresh the base only starting from the modified node!
            RefreshBase(baseGraph);
            var rootNode = (AssetNode)assetContent.OwnerNode;
            var visitor = CreateReconcilierVisitor();
            visitor.Visiting += (node, path) => ReconcileWithBaseNode((AssetNode)node);
            visitor.Visit(rootNode);
            UpdatingPropertyFromBase = false;

            BaseContentChanged?.Invoke(e, assetContent);
        }

        private void ReconcileWithBaseNode(AssetNode assetNode)
        {
            if (assetNode.Content is ObjectContent || assetNode.BaseContent == null || !assetNode.CanOverride)
                return;

            var baseNode = (AssetNode)assetNode.BaseContent.OwnerNode;
            var localValue = assetNode.Content.Retrieve();
            var baseValue = assetNode.BaseContent.Retrieve();

            // Reconcile occurs only when the node is not overridden.
            if (!assetNode.IsContentOverridden())
            {
                assetNode.ResettingOverride = true;
                // Handle null cases first
                if (localValue == null || baseValue == null)
                {
                    if (localValue == null && baseValue != null)
                    {
                        var clonedValue = CloneValueFromBase(baseValue, assetNode);
                        assetNode.Content.Update(clonedValue);
                    }
                    else if (localValue != null /*&& baseValue == null*/)
                    {
                        assetNode.Content.Update(null);
                    }
                }
                // Then handle collection and dictionary cases
                else if (assetNode.Content.Descriptor is CollectionDescriptor || assetNode.Content.Descriptor is DictionaryDescriptor)
                {
                    // Items to add and to remove are stored in local collections and processed later, since they might affect indices
                    var itemsToRemove = new List<ItemId>();
                    var itemsToAdd = new SortedList<object, ItemId>(new DefaultKeyComparer());

                    // Check for item present in the instance and absent from the base.
                    foreach (var index in assetNode.Content.Indices)
                    {
                        // Skip overridden items
                        if (assetNode.IsItemOverridden(index))
                            continue;

                        var itemId = assetNode.IndexToId(index);
                        if (itemId != ItemId.Empty)
                        {
                            // Look if an item with the same id exists in the base.
                            if (!baseNode.HasId(itemId))
                            {
                                // If not, remove this item from the instance.
                                itemsToRemove.Add(itemId);
                            }
                        }
                        else
                        {
                            // This case should not happen, but if we have an empty id due to corrupted data let's just remove the item.
                            itemsToRemove.Add(itemId);
                        }
                    }

                    // Clean items marked as "override-deleted" that are absent from the base.
                    var ids = CollectionItemIdHelper.GetCollectionItemIds(localValue);
                    foreach (var deletedId in ids.DeletedItems.ToList())
                    {
                        if (assetNode.BaseContent.Indices.All(x => baseNode.IndexToId(x) != deletedId))
                        {
                            ids.UnmarkAsDeleted(deletedId);
                        }
                    }

                    // Add item present in the base and missing here, and also update items that have different values between base and instance
                    foreach (var index in assetNode.BaseContent.Indices)
                    {
                        var itemId = baseNode.IndexToId(index);
                        // TODO: What should we do if it's empty? It can happen only from corrupted data

                        // Skip items marked as "override-deleted"
                        if (itemId == ItemId.Empty || assetNode.IsItemDeleted(itemId))
                            continue;

                        Index localIndex;
                        if (!assetNode.TryIdToIndex(itemId, out localIndex))
                        {
                            // For dictionary, we might have a key collision, if so, we consider that the new value from the base is deleted in the instance.
                            var keyCollision = assetNode.Content.Descriptor is DictionaryDescriptor && (assetNode.Content.Reference?.HasIndex(index) == true || assetNode.Content.Indices.Any(x => index.Equals(x)));
                            // For specific collections (eg. EntityComponentCollection) it might not be possible to add due to other kinds of collisions or invalid value.
                            var itemRejected = !CanUpdate(assetNode, ContentChangeType.CollectionAdd, localIndex, baseNode.Content.Retrieve(index));

                            // We cannot add the item, let's mark it as deleted.
                            if (keyCollision || itemRejected)
                            {
                                var instanceIds = CollectionItemIdHelper.GetCollectionItemIds(assetNode.Content.Retrieve());
                                instanceIds.MarkAsDeleted(itemId);
                            }
                            else
                            {
                                // Add it if the key is available for add
                                itemsToAdd.Add(index.Value, itemId);
                            }
                        }
                        else
                        {
                            // If the item is present in both the instance and the base, check if we need to reconcile the value
                            var member = assetNode.Content as MemberContent;
                            var targetNode = assetNode.Content.Reference?.AsEnumerable?[localIndex]?.TargetNode;
                            // Skip it if it's overridden
                            if (!assetNode.IsItemOverridden(localIndex))
                            {
                                var localItemValue = assetNode.Content.Retrieve(localIndex);
                                var baseItemValue = baseNode.Content.Retrieve(index);
                                if (ShouldReconcileItem(member, targetNode, localItemValue, baseItemValue, assetNode.Content.Reference is ReferenceEnumerable))
                                {
                                    var clonedValue = CloneValueFromBase(baseItemValue, assetNode);
                                    assetNode.Content.Update(clonedValue, localIndex);
                                }
                            }
                            // In dictionaries, the keys might be different between the instance and the base. We need to reconcile them too
                            if (assetNode.Content.Descriptor is DictionaryDescriptor && !assetNode.IsKeyOverridden(localIndex))
                            {
                                if (ShouldReconcileItem(member, targetNode, localIndex.Value, index.Value, false))
                                {
                                    // Reconcile using a move (Remove + Add) of the key-value pair
                                    var clonedIndex = new Index(CloneValueFromBase(index.Value, assetNode));
                                    var localItemValue = assetNode.Content.Retrieve(localIndex);
                                    assetNode.Content.Remove(localItemValue, localIndex);
                                    assetNode.Content.Add(localItemValue, clonedIndex);
                                    ids[clonedIndex.Value] = itemId;
                                }
                            }
                        }
                    }

                    // Process items marked to be removed
                    foreach (var item in itemsToRemove)
                    {
                        var index = assetNode.IdToIndex(item);
                        var value = assetNode.Content.Retrieve(index);
                        assetNode.Content.Remove(value, index);
                        // We're reconciling, so let's hack the normal behavior of marking the removed item as deleted.
                        ids.UnmarkAsDeleted(item);
                    }

                    // Process items marked to be added
                    foreach (var item in itemsToAdd)
                    {
                        var baseIndex = baseNode.IdToIndex(item.Value);
                        var baseItemValue = baseNode.Content.Retrieve(baseIndex);
                        var clonedValue = CloneValueFromBase(baseItemValue, assetNode);
                        if (assetNode.Content.Descriptor is CollectionDescriptor)
                        {
                            // In a collection, we need to find an index that matches the index on the base to maintain order.
                            // To do so, we iterate from the index in the base to zero.
                            var currentBaseIndex = baseIndex.Int - 1;

                            // Initialize the target index to zero, in case we don't find any better index.
                            var localIndex = new Index(0);

                            // Find the first item of the base that also exists (in term of id) in the local node, iterating backward (from baseIndex to 0)
                            while (currentBaseIndex >= 0)
                            {
                                ItemId baseId;
                                // This should not happen since the currentBaseIndex comes from the base.
                                if (!baseNode.TryIndexToId(new Index(currentBaseIndex), out baseId))
                                    throw new InvalidOperationException("Cannot find an identifier matching the index in the base collection");

                                Index sameIndexInInstance;
                                // If we have an matching item, we want to insert right after it
                                if (assetNode.TryIdToIndex(baseId, out sameIndexInInstance))
                                {
                                    localIndex = new Index(sameIndexInInstance.Int + 1);
                                    break;
                                }
                                currentBaseIndex--;
                            }

                                assetNode.Restore(clonedValue, localIndex, item.Value);
                        }
                        else
                        {
                            // This case is for dictionary. Key collisions have already been handle at that point so we can directly do the add without further checks.
                            assetNode.Restore(clonedValue, baseIndex, item.Value);
                        }
                    }
                }
                // Finally, handle single properties
                else
                {
                    var member = assetNode.Content as MemberContent;
                    var targetNode = assetNode.Content.Reference?.AsObject?.TargetNode;
                    if (ShouldReconcileItem(member, targetNode, localValue, baseValue, assetNode.Content.Reference is ObjectReference))
                    {
                        var clonedValue = CloneValueFromBase(baseValue, assetNode);
                        assetNode.Content.Update(clonedValue);
                    }
                }
                assetNode.ResettingOverride = false;
            }
        }

        protected virtual bool ShouldReconcileItem(MemberContent member, IGraphNode targetNode, object localValue, object baseValue, bool isReference)
        {
            if (isReference)
            {
                // Reference type, we check matches by type
                return baseValue?.GetType() != localValue?.GetType();
            }

            // Content reference (note: they are not treated as reference
            if (AssetRegistry.IsContentType(localValue?.GetType()) || AssetRegistry.IsContentType(localValue?.GetType()))
            {
                var localRef = AttachedReferenceManager.GetAttachedReference(localValue);
                var baseRef = AttachedReferenceManager.GetAttachedReference(baseValue);
                return localRef?.Id != baseRef?.Id || localRef?.Url != baseRef?.Url;
            }
            
            // Value type, we check for equality
            return !Equals(localValue, baseValue);
        }
    }
}
