using Xunit;

// D3D12 debug-layer enablement is process-global and must happen before device creation.
// Running debug and non-debug WARP-device tests concurrently makes that native precondition
// nondeterministic, so this backend integration suite deliberately serializes device lifetimes.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
