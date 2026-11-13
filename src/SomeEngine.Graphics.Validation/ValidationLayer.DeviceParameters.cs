namespace SomeEngine.Graphics.Validation;
public sealed partial class ValidationLayer
{
    private static void ValidateDeviceDescription(in DeviceDesc desc, bool dxil)
    {
        if (desc.Queues.IsEmpty)
            throw new ArgumentException("A Device requires at least one Queue description.", nameof(desc));
        if (desc.EnabledNodeMask == 0)
            throw new ArgumentOutOfRangeException(nameof(desc), "EnabledNodeMask must be nonzero.");
        if (!dxil && desc.EnabledNodeMask != 1)
            throw new NotSupportedException("The Vulkan device exposes one adapter node.");
        const DeviceFeatures knownFeatures = DeviceFeatures.Presentation | DeviceFeatures.SparseResources | DeviceFeatures.SamplerFeedback | DeviceFeatures.Residency | DeviceFeatures.RayTracing | DeviceFeatures.MeshShaders | DeviceFeatures.VariableRateShading | DeviceFeatures.WorkGraphs | DeviceFeatures.IndirectCommands | DeviceFeatures.CalibratedTimestamps | DeviceFeatures.LinkedAdapters | DeviceFeatures.ExternalResources | DeviceFeatures.ExternalTimelines;
        if (((desc.RequiredFeatures | desc.OptionalFeatures) & ~knownFeatures) != 0)
            throw new ArgumentOutOfRangeException(nameof(desc), "The Device feature set contains unknown bits.");
        for (int queueIndex = 0; queueIndex < desc.Queues.Length; queueIndex++)
        {
            ref readonly DeviceQueueDesc queue = ref desc.Queues[queueIndex];
            _ = queue.Type switch
            {
                QueueType.Graphics => 0,
                QueueType.Compute => 1,
                QueueType.Copy => 2,
                _ => throw new ArgumentOutOfRangeException(nameof(desc), "A Queue type is unknown."),
            };
            if (queue.Count == 0)
                throw new ArgumentOutOfRangeException(nameof(desc), "A Queue count must be nonzero.");
            if (!dxil && queue.NodeIndex != 0)
                throw new NotSupportedException("Vulkan queues use adapter node zero.");
            if (!float.IsFinite(queue.Priority) || queue.Priority is < 0f or > 1f)
                throw new ArgumentOutOfRangeException(nameof(desc), "A Queue priority must be finite and in [0, 1].");
            if (queue.NodeIndex >= 32 || (desc.EnabledNodeMask & (1u << checked((int)queue.NodeIndex))) == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(desc), "A Queue NodeIndex must select one enabled linked-adapter node.");
            }

            for (int priorIndex = 0; priorIndex < queueIndex; priorIndex++)
            {
                ref readonly DeviceQueueDesc prior = ref desc.Queues[priorIndex];
                if (dxil && prior.Type == queue.Type && prior.NodeIndex == queue.NodeIndex)
                {
                    throw new ArgumentException("A Device Queue type and linked-adapter node may be described only once; use Count for multiple Queues.", nameof(desc));
                }
            }
        }
    }
}
