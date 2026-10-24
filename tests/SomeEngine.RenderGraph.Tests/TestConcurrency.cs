using Xunit;

// EngineTrace is process-wide. A capture from another test changes both recorded
// events and allocations, so graph behavior and allocation checks run in isolation.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
