using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.ECS.Systems;

public static partial class HierarchyPropagationAdapter<TDomain>
    where TDomain : IHierarchyDomain
{
    private readonly struct HierarchyTraversalCapture
    {
        internal HierarchyTraversalCapture(
            ReadOnlyMemory<ReadOnlyMemory<TraversalNode>> packetNodes,
            ReadOnlyMemory<HierarchyPropagationEntityAddress> entityAddresses,
            ReadOnlyMemory<HierarchyPropagationEntityAddress> currentEntityAddresses,
            ReadOnlyMemory<HierarchyPropagationEntityAddress> externalAncestorEntityAddresses,
            ulong fingerprint)
        {
            PacketNodes = packetNodes;
            EntityAddresses = entityAddresses;
            CurrentEntityAddresses = currentEntityAddresses;
            ExternalAncestorEntityAddresses = externalAncestorEntityAddresses;
            Fingerprint = fingerprint;
        }

        internal ReadOnlyMemory<ReadOnlyMemory<TraversalNode>> PacketNodes { get; }

        internal ReadOnlyMemory<HierarchyPropagationEntityAddress> EntityAddresses { get; }

        internal ReadOnlyMemory<HierarchyPropagationEntityAddress> CurrentEntityAddresses { get; }

        internal ReadOnlyMemory<HierarchyPropagationEntityAddress>
            ExternalAncestorEntityAddresses { get; }

        internal ulong Fingerprint { get; }
    }

    private readonly struct TraversalNode
    {
        internal TraversalNode(
            Entity entity,
            long stableAddress,
            Entity parent,
            Entity root,
            int depth)
        {
            Entity = entity;
            StableAddress = stableAddress;
            Parent = parent;
            Root = root;
            Depth = depth;
        }

        internal Entity Entity { get; }

        internal long StableAddress { get; }

        internal Entity Parent { get; }

        internal Entity Root { get; }

        internal int Depth { get; }
    }
}
