using Xunit;

// Prevent concurrent rendering tests in this assembly from starving CPU-timed frame benchmarks
// or triggering cross-thread GC during GC.GetAllocatedBytesForCurrentThread() zero-alloc checks.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
