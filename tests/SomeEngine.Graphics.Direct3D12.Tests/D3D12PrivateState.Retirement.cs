using System.Collections;
using System.Reflection;

namespace SomeEngine.Graphics.Direct3D12.Tests;

internal static unsafe partial class D3D12PrivateState
{
    internal static ulong NextCompletion(Queue queue) =>
        (ulong)GetField(queue, "_nextCompletion").GetValue(queue)!;

    internal static void SetNextCompletion(Queue queue, ulong value) =>
        GetField(queue, "_nextCompletion").SetValue(queue, value);

    internal static QueueRetirementSnapshot QueueRetirements(Queue queue)
    {
        IList pendingSubmissions = (IList)GetField(queue, "_pendingSubmissions")
            .GetValue(queue)!;
        IList untrustedSubmissions = (IList)GetField(queue, "_untrustedSubmissions")
            .GetValue(queue)!;
        ChainSnapshot presentation = InspectChain(
            GetField(queue, "_pendingPresentationRetirements").GetValue(queue)!);
        ChainSnapshot untrustedPresentation = InspectChain(
            GetField(queue, "_untrustedPresentationRetirements").GetValue(queue)!);
        ChainSnapshot capability = InspectChain(
            GetField(queue, "_pendingCapabilityPayloads").GetValue(queue)!);
        ChainSnapshot untrustedCapability = InspectChain(
            GetField(queue, "_untrustedCapabilityPayloads").GetValue(queue)!);

        return new QueueRetirementSnapshot(
            pendingSubmissions.Count,
            untrustedSubmissions.Count,
            presentation.Count,
            untrustedPresentation.Count,
            capability.Count,
            untrustedCapability.Count,
            SubmissionTarget(pendingSubmissions),
            presentation.Target,
            capability.Target,
            presentation.NativeReferenceCount + untrustedPresentation.NativeReferenceCount,
            capability.NativeReferenceCount + untrustedCapability.NativeReferenceCount,
            PointerPropertyIsNonZero(queue, "Native"),
            PointerPropertyIsNonZero(queue, "Fence"));
    }

    private static ulong SubmissionTarget(IList submissions)
    {
        if (submissions.Count == 0)
            return 0;
        object last = submissions[submissions.Count - 1]!;
        return (ulong)GetProperty(last, "Completion").GetValue(last)!;
    }

    private static ChainSnapshot InspectChain(object chain)
    {
        object? current = GetField(chain, "_head").GetValue(chain);
        int count = 0;
        int nativeReferences = 0;
        ulong target = 0;
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        while (current is not null)
        {
            if (!visited.Add(current))
                throw new InvalidOperationException("The retirement chain contains a cycle.");
            count = checked(count + 1);
            target = (ulong)GetField(current, "RetirementCompletion").GetValue(current)!;
            nativeReferences += CountNativeReferences(current);
            current = GetField(current, "RetirementNext").GetValue(current);
        }
        return new ChainSnapshot(count, target, nativeReferences);
    }

    private static int CountNativeReferences(object payload)
    {
        int count = 0;
        FieldInfo? swapchain = TryGetField(payload.GetType(), "_swapchain");
        if (swapchain?.GetValue(payload) is Pointer pointer &&
            Pointer.Unbox(pointer) is not null)
        {
            count++;
        }

        FieldInfo? images = TryGetField(payload.GetType(), "_images");
        if (images?.GetValue(payload) is Array imageArray)
            count += imageArray.Length;
        FieldInfo? lifetimes = TryGetField(payload.GetType(), "_lifetimes");
        object? retainedLifetimes = lifetimes?.GetValue(payload);
        if (retainedLifetimes is Array lifetimeArray)
            count += lifetimeArray.Length;
        else if (retainedLifetimes is not null)
            count += (int)GetProperty(retainedLifetimes, "Count").GetValue(retainedLifetimes)!;
        return count;
    }

    private readonly record struct ChainSnapshot(
        int Count,
        ulong Target,
        int NativeReferenceCount);
}

internal readonly record struct QueueRetirementSnapshot(
    int PendingSubmissionCount,
    int UntrustedSubmissionCount,
    int PendingPresentationCount,
    int UntrustedPresentationCount,
    int PendingCapabilityCount,
    int UntrustedCapabilityCount,
    ulong SubmissionTarget,
    ulong PresentationTarget,
    ulong CapabilityTarget,
    int PresentationNativeReferenceCount,
    int CapabilityNativeReferenceCount,
    bool HasNativeQueue,
    bool HasFence);
