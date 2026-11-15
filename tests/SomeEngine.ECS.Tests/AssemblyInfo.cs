using Xunit;

// The fuzz tests ran one class at a time in their own project; the merged assembly keeps that.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
