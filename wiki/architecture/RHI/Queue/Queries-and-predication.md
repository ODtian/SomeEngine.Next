# RHI-QUE-009 — Queries and predication 

Query pools are typed by query kind and Queue family. Begin/end queries, timestamps, resolves and
Pipeline/stream-output statistics use explicit slots and destination Buffer ranges. Resolve checks
query count, destination stride, usage and bounds before recording.

Predication names a Buffer, offset and operation. Clearing predication passes a null Buffer. Query and
predication operations do not add barriers or waits; the caller supplies synchronization through the
ordinary barrier and Queue mechanisms.
^rhi-que-009


Vulkan maps its Copy queues to a native family that supports transfer and graphics or compute commands, preferring a compute family without graphics. This covers the public Copy timestamp reset and resolve sequence on the same queue and query pool. The implementation is [QueuePlan](../../../../src/SomeEngine.Graphics.Vulkan/VulkanDevice.cs); the public readback check is [Timestamp_query_resolves_to_buffer](../../../../tests/SomeEngine.Graphics.Vulkan.Tests/VulkanCoreFeatureTests.cs).
