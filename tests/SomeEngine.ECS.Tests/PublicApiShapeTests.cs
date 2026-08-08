using System.Reflection;
using System.Runtime.CompilerServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Tests;

public sealed class PublicApiShapeTests
{
    [Fact]
    public void RootComponentContractsHaveNoCompatibilityAliases()
    {
        Assembly assembly = typeof(World).Assembly;
        Assert.Null(assembly.GetType(
            "SomeEngine.ECS.Components.IComponent",
            throwOnError: false));
        Assert.Null(assembly.GetType(
            "SomeEngine.ECS.Components.IEnableableComponent",
            throwOnError: false));
        Assert.Null(assembly.GetType(
            "SomeEngine.ECS.Components.ICleanupComponent",
            throwOnError: false));
    }

    [Fact]
    public void World_ExposesOnlyRuntimeOwnedPublicQueryExecution()
    {
        MethodInfo[] declaredMethods = typeof(World).GetMethods(
            BindingFlags.Instance
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly);
        MethodInfo[] publicMethods = declaredMethods.Where(static method => method.IsPublic).ToArray();

        Assert.DoesNotContain(declaredMethods, method => method.Name == "RunQuery");
        Assert.DoesNotContain(declaredMethods, method => method.Name == "RunReadWrite");
        Assert.DoesNotContain(declaredMethods, method => method.Name == "GetQueryState");
        Assert.DoesNotContain(declaredMethods, method => method.Name == "CreateQuery");
        Assert.DoesNotContain(declaredMethods, method => method.Name == "RegisterQuery");
        Assert.DoesNotContain(publicMethods, method => method.Name == "Get");
        Assert.DoesNotContain(publicMethods, method => method.Name == "ReadRef");
        Assert.DoesNotContain(publicMethods, method => method.Name == "GetBuffer");
        Assert.DoesNotContain(publicMethods, method => method.Name == "GetSparse");
        Assert.DoesNotContain(publicMethods, method => method.Name == "GetSparseSet");
        Assert.DoesNotContain(publicMethods, static method =>
            method.Name == "ExecuteQuery" &&
            method.GetParameters().Count(static parameter => parameter.ParameterType == typeof(uint)) > 1);
        Assert.Contains(publicMethods, method => method.Name == "ExecuteQuery");
        Assert.Contains(publicMethods, method => method.Name == "ExecuteReadWrite");
        Assert.Contains(publicMethods, method => method.Name == "ExecuteBufferRead");
        Assert.Contains(publicMethods, method => method.Name == "ExecuteBufferWrite");
        Assert.Contains(publicMethods, method => method.Name == "ReadSparse");
        Assert.Contains(publicMethods, method => method.Name == "ExecuteSparseRead");
        Assert.Contains(publicMethods, method => method.Name == "ExecuteSparseWrite");
    }

    [Fact]
    public void SparseBorrows_AreCallbackScopedAndStateCannotCarryRefStructs()
    {
        MethodInfo[] publicMethods = typeof(World).GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(publicMethods, static method =>
            method.Name.Contains("Sparse", StringComparison.Ordinal) &&
            (method.ReturnType.IsByRef || method.ReturnType.IsByRefLike));

        MethodInfo[] stateOverloads = publicMethods
            .Where(static method =>
                method.Name is "ExecuteSparseRead" or "ExecuteSparseWrite" &&
                method.IsGenericMethodDefinition &&
                method.GetGenericArguments().Length == 2)
            .ToArray();
        Assert.Equal(2, stateOverloads.Length);
        foreach (MethodInfo overload in stateOverloads)
        {
            Type stateParameter = overload.GetGenericArguments()[1];
            Assert.DoesNotContain(
                stateParameter.CustomAttributes,
                static attribute => attribute.AttributeType.Name == "AllowsRefLikeAttribute");
        }
    }

    [Fact]
    public void BufferBorrows_AreCallbackScopedAndStateCannotCarryRefStructs()
    {
        Assert.True(typeof(BufferView<>).IsByRefLike);
        Assert.True(typeof(DynamicBuffer<>).IsByRefLike);

        MethodInfo[] publicMethods = typeof(World).GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(publicMethods, static method =>
            IsConstructedFrom(method.ReturnType, typeof(BufferView<>)) ||
            IsConstructedFrom(method.ReturnType, typeof(DynamicBuffer<>)));

        MethodInfo[] stateOverloads = publicMethods
            .Where(static method =>
                method.Name is "ExecuteBufferRead" or "ExecuteBufferWrite" &&
                method.IsGenericMethodDefinition &&
                method.GetGenericArguments().Length == 2)
            .ToArray();
        Assert.Equal(2, stateOverloads.Length);
        foreach (MethodInfo overload in stateOverloads)
        {
            Type stateParameter = overload.GetGenericArguments()[1];
            Assert.DoesNotContain(
                stateParameter.CustomAttributes,
                static attribute => attribute.AttributeType.Name == "AllowsRefLikeAttribute");
        }
    }

    [Fact]
    public void DynamicBufferInlineStorage_IsTheComponentInsteadOfAWrappedSecondLayout()
    {
        Type[] assemblyTypes = typeof(World).Assembly.GetTypes();
        Assert.DoesNotContain(
            assemblyTypes,
            static type => WithoutGenericArity(type.Name) == "BufferInlineStorage");

        Type storage = typeof(Components.DynamicBufferInline<>);
        InlineArrayAttribute attribute =
            Assert.IsType<InlineArrayAttribute>(
                storage.GetCustomAttribute<InlineArrayAttribute>());
        Assert.Equal(8, attribute.Length);

        FieldInfo element = Assert.Single(storage.GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.Equal("_element0", element.Name);
        Assert.True(element.FieldType.IsGenericParameter);
    }

    [Fact]
    public void BundleWrites_AreRuntimeOwnedAndRemovedEscapesStayDeleted()
    {
        Assert.True(typeof(BundleWriteView).IsByRefLike);

        MethodInfo[] publicMethods = typeof(World).GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        string[] removedMethodNames =
        [
            string.Concat("Create", "Spawn", "Writer"),
            string.Concat("Create", "Add", "Writer"),
            string.Concat("Create", "Replace", "Writer"),
            string.Concat("Create", "Load", "Writer"),
            string.Concat("Spawn", "Batch"),
            string.Concat("Shared", "Value"),
        ];
        foreach (string removedMethodName in removedMethodNames)
        {
            Assert.DoesNotContain(
                publicMethods,
                method => string.Equals(method.Name, removedMethodName, StringComparison.Ordinal));
        }

        Assert.Contains(publicMethods, static method => method.Name == "ExecuteBundleSpawn");
        Assert.Contains(publicMethods, static method => method.Name == "ExecuteBundleAdd");
        Assert.Contains(publicMethods, static method => method.Name == "ExecuteBundleReplace");
        Assert.Contains(publicMethods, static method => method.Name == "ExecuteBundleSpawnBatch");

        MethodInfo[] stateOverloads = publicMethods
            .Where(static method =>
                method.Name is "ExecuteBundleSpawn" or
                    "ExecuteBundleAdd" or
                    "ExecuteBundleReplace" or
                    "ExecuteBundleSpawnBatch" &&
                method.IsGenericMethodDefinition)
            .ToArray();
        Assert.Equal(8, stateOverloads.Length);
        foreach (MethodInfo overload in stateOverloads)
        {
            Type stateParameter = Assert.Single(overload.GetGenericArguments());
            Assert.DoesNotContain(
                stateParameter.CustomAttributes,
                static attribute => attribute.AttributeType.Name == "AllowsRefLikeAttribute");
        }

        string[] removedTypeNames =
        [
            string.Concat("Bundle", "Writer"),
            string.Concat("Bundle", "Batch"),
            string.Concat("Bundle", "Batch", "Chunk"),
            string.Concat("Shared", "Value", "Slot"),
        ];
        Type[] assemblyTypes = typeof(World).Assembly.GetTypes();
        foreach (string removedTypeName in removedTypeNames)
        {
            Assert.DoesNotContain(
                assemblyTypes,
                type => WithoutGenericArity(type.Name) == removedTypeName);
        }
    }

    [Fact]
    public void EcsAssembly_DoesNotContainRemovedQueryFacadeTypes()
    {
        Type[] assemblyTypes = typeof(World).Assembly.GetTypes();

        Assert.DoesNotContain(
            assemblyTypes,
            static type => WithoutGenericArity(type.Name) is "QueryBuilder" or "QueryView");
    }

    [Fact]
    public void EcsAssembly_DoesNotExportRemovedRelationshipModels()
    {
        string[] removedTypeNames =
        [
            "IRelation",
            "IExclusiveRelation",
            "RelationStore",
            "RelationTag",
            "ChildBuffer",
            "HierarchyLink",
            "HierarchyParent",
            "HierarchyNode",
            "QueryBuilder",
            "QueryView",
            "QueryState",
        ];

        Type[] exportedTypes = typeof(World).Assembly.GetExportedTypes();
        foreach (string removedTypeName in removedTypeNames)
        {
            Assert.DoesNotContain(
                exportedTypes,
                type => WithoutGenericArity(type.Name) == removedTypeName);
        }
    }

    [Fact]
    public void EcsAssembly_ExportsCanonicalParentAndChildrenComponents()
    {
        Type[] exportedTypes = typeof(World).Assembly.GetExportedTypes();

        Assert.Contains(exportedTypes, type => type == typeof(Parent<>));
        Assert.Contains(exportedTypes, type => type == typeof(Children<>));
        Assert.Empty(typeof(Children<ApiHierarchyDomain>).GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
    }

    [Fact]
    public void ExportedViewsAreCallbackScopedByRefLikesAndStoredGenerationsAreSnapshots()
    {
        Assembly assembly = typeof(World).Assembly;
        Type[] exportedViews = assembly.GetExportedTypes()
            .Where(static type =>
                WithoutGenericArity(type.Name).EndsWith("View", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(exportedViews);
        Assert.All(
            exportedViews,
            static type => Assert.True(
                type.IsByRefLike,
                $"{type.FullName} is named View but can escape its borrow scope."));
        Assert.Null(assembly.GetType(
            "SomeEngine.ECS.Hierarchy.HierarchyChildrenView`1",
            throwOnError: false));

        Type snapshot = typeof(HierarchyChildrenSnapshot<ApiHierarchyDomain>);
        Assert.False(snapshot.IsByRefLike);
        FieldInfo memory = Assert.Single(
            snapshot.GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly),
            static field => field.FieldType == typeof(ReadOnlyMemory<Entity>));
        Assert.Equal(typeof(ReadOnlyMemory<Entity>), memory.FieldType);
        Assert.Equal("_items", memory.Name);
        Assert.Equal(typeof(ReadOnlySpan<Entity>), snapshot.GetProperty("Span")!.PropertyType);
    }

    [Fact]
    public void PublicToArrayMethodsAreExplicitSnapshotMaterializationBoundaries()
    {
        MethodInfo[] materializers = typeof(World).Assembly.GetExportedTypes()
            .SelectMany(static type => type.GetMethods(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.DeclaredOnly))
            .Where(static method =>
                method.Name == "ToArray" &&
                method.GetParameters().Length == 0)
            .ToArray();

        Assert.Equal(2, materializers.Length);
        Assert.Contains(
            materializers,
            static method =>
                method.DeclaringType == typeof(HierarchyChildrenSnapshot<>) &&
                method.ReturnType == typeof(Entity[]));
        Assert.Contains(
            materializers,
            static method =>
                method.DeclaringType == typeof(RelationEdgeQuery<>) &&
                method.ReturnType.IsArray &&
                method.ReturnType.GetElementType()!.IsGenericType &&
                method.ReturnType.GetElementType()!.GetGenericTypeDefinition() ==
                typeof(RelationEdge<>));
    }

    private readonly struct ApiHierarchyDomain : IHierarchyDomain;

    private static string WithoutGenericArity(string name)
    {
        int marker = name.IndexOf('`');
        return marker < 0 ? name : name[..marker];
    }

    private static bool IsConstructedFrom(Type type, Type genericDefinition) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == genericDefinition;
}
