using System.Reflection;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class GeneratedJobEntityApiShapeTests
{
    [Fact]
    public void RowBorrowIsByRefLikeAndSparseWrapperTypesAreAbsent()
    {
        Assert.True(typeof(JobEntityRow).IsByRefLike);

        Assert.Empty(typeof(JobEntityRow).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.DoesNotContain(
            typeof(JobEntityRow).Assembly.GetTypes(),
            static type => type.Name is "SparseRefRO`1" or "SparseRefRW`1");
    }

    [Fact]
    public void StoredDescriptorAndProof_DoNotContainWorldOrStorageBorrows()
    {
        AssertNoBorrowFields(typeof(GeneratedQueryAccessDescriptor));
        AssertNoBorrowFields(typeof(GeneratedQueryAccess));
        AssertNoBorrowFields(typeof(StableQueryPartitionProof));
        AssertNoBorrowFields(typeof(StableQueryPacketRange));
    }

    [Fact]
    public void MarkerInterface_DoesNotExposeAnUnscopedExecutionPrimitive()
    {
        Assert.Empty(typeof(IJobEntity).GetMethods());
        Assert.Empty(typeof(IJobEntity).GetProperties());
    }

    [Fact]
    public void RoslynAliasCertificateFactories_AreNotPubliclyCallable()
    {
        const BindingFlags publicStatic = BindingFlags.Public | BindingFlags.Static;

        Assert.Null(typeof(GeneratedQueryAccess).GetMethod("GeneratedTable", publicStatic));
        Assert.Null(typeof(GeneratedQueryAccess).GetMethod("GeneratedBuffer", publicStatic));
        Assert.Null(typeof(GeneratedQueryAccess).GetMethod("GeneratedSparse", publicStatic));
    }

    [Fact]
    public void ScheduledJobCarriers_RetainCollectionsThroughMemoryInsteadOfRawArrays()
    {
        Type[] scheduledCarriers = typeof(JobEntityRuntime).Assembly.GetTypes()
            .Where(static type =>
                type.IsValueType &&
                (typeof(IJob).IsAssignableFrom(type) ||
                 typeof(IJobParallelFor).IsAssignableFrom(type)))
            .ToArray();

        Assert.NotEmpty(scheduledCarriers);
        foreach (Type carrier in scheduledCarriers)
        {
            foreach (FieldInfo field in carrier.GetFields(
                         BindingFlags.Public |
                         BindingFlags.NonPublic |
                         BindingFlags.Instance))
            {
                Assert.False(
                    field.FieldType.IsArray,
                    $"{carrier.FullName}.{field.Name} retains raw array {field.FieldType}; " +
                    "a scheduled borrow must use Memory/ReadOnlyMemory.");
            }
        }
    }

    private static void AssertNoBorrowFields(Type type)
    {
        foreach (FieldInfo field in type.GetFields(
                     BindingFlags.Public |
                     BindingFlags.NonPublic |
                     BindingFlags.Instance))
        {
            Assert.False(field.FieldType.IsByRefLike, $"{type.Name}.{field.Name} stores a byref-like value.");
            Assert.False(
                ContainsType(field.FieldType, typeof(World)),
                $"{type.Name}.{field.Name} retains World through {field.FieldType}.");
            Assert.False(
                ContainsTypeNamed(field.FieldType, "SomeEngine.ECS.Queries.QueryRecord"),
                $"{type.Name}.{field.Name} retains a registry-owned QueryRecord through {field.FieldType}.");
            Assert.DoesNotContain("Chunk", field.FieldType.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("Span", field.FieldType.Name, StringComparison.Ordinal);
        }
    }

    private static bool ContainsType(Type candidate, Type expected)
    {
        if (candidate == expected)
            return true;
        if (candidate.HasElementType)
            return ContainsType(candidate.GetElementType()!, expected);
        return candidate.IsGenericType &&
               candidate.GetGenericArguments().Any(argument => ContainsType(argument, expected));
    }

    private static bool ContainsTypeNamed(Type candidate, string expectedFullName)
    {
        if (candidate.FullName == expectedFullName)
            return true;
        if (candidate.HasElementType)
            return ContainsTypeNamed(candidate.GetElementType()!, expectedFullName);
        return candidate.IsGenericType &&
               candidate.GetGenericArguments().Any(
                   argument => ContainsTypeNamed(argument, expectedFullName));
    }
}
