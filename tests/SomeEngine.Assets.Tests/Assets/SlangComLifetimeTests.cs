using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.Marshalling;
using SlangShaderSharp;
using SomeEngine.Assets.Importers;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class SlangComLifetimeTests
{
    [Fact]
    public void ImporterMetadataUsesGeneratedComInterfaceAndUniqueOwnership()
    {
        Assert.NotNull(typeof(IMetadata).GetCustomAttribute<GeneratedComInterfaceAttribute>());
        AssertMetadataInterface(
            Parameter(typeof(IComponentType), nameof(IComponentType.GetTargetMetadata), 1));
        AssertMetadataInterface(
            Parameter(typeof(IComponentType), nameof(IComponentType.GetEntryPointMetadata), 2));
    }

    [Fact]
    public void SlangBindingsMatchNativeOwnedAndBorrowedComResults()
    {
        AssertMarshaller(
            Method(typeof(IComponentType), nameof(IComponentType.GetSession)).ReturnParameter,
            "NoFreeComInterfaceMarshaller`1");
        AssertMarshaller(
            Method(typeof(ISession), nameof(ISession.GetGlobalSession)).ReturnParameter,
            "NoFreeComInterfaceMarshaller`1");

        MethodInfo createSession = Method(typeof(IGlobalSession), nameof(IGlobalSession.CreateSession));
        AssertUniqueMarshaller(createSession.GetParameters()[1]);
        MethodInfo loadModule = Method(typeof(ISession), nameof(ISession.LoadModuleFromSource));
        AssertUniqueMarshaller(loadModule.ReturnParameter);
        AssertUniqueMarshaller(loadModule.GetParameters()[3]);
        MethodInfo compose = Method(
            typeof(ISession),
            nameof(ISession.CreateCompositeComponentType));
        AssertUniqueMarshaller(compose.GetParameters()[2]);
        AssertUniqueMarshaller(compose.GetParameters()[3]);
        MethodInfo link = Method(typeof(IComponentType), nameof(IComponentType.Link));
        AssertUniqueMarshaller(link.GetParameters()[0]);
        AssertUniqueMarshaller(link.GetParameters()[1]);

        Assert.Equal(
            typeof(IGlobalSession),
            Method(typeof(FunctionReflection), nameof(FunctionReflection.FindAttributeByName))
                .GetParameters()[0].ParameterType);
        Assert.Equal(
            typeof(IGlobalSession),
            Method(typeof(VariableReflection), nameof(VariableReflection.FindAttributeByName))
                .GetParameters()[0].ParameterType);
        Assert.Equal(16u, (uint)SlangStage.Node);

        MethodInfo resultOverload = Assert.Single(
            typeof(AttributeReflection).GetMethods(),
            static method =>
                method.Name == nameof(AttributeReflection.GetArgumentValueInt) &&
                method.ReturnType == typeof(SlangResult));
        Assert.Equal(typeof(int).MakeByRefType(), resultOverload.GetParameters()[1].ParameterType);
    }

    [Fact]
    public void TrackedBlobIsInvalidAfterLifetimeDispose()
    {
        ISlangBlob blob = Slang.CreateBlob([0x5A]);
        using (var lifetime = new SlangImportLifetime())
            lifetime.Track(blob);

        Assert.Throws<ObjectDisposedException>(() => blob.GetBufferSize());
    }

    [Fact]
    public void TrackedSessionIsInvalidAfterLifetimeDispose()
    {
        IGlobalSession global = SlangShaderImporter.GlobalSession;
        Assert.True(global.CreateSession(new SessionDesc(), out ISession session).Succeeded);
        using (var lifetime = new SlangImportLifetime())
            lifetime.Track(session);

        Assert.Throws<ObjectDisposedException>(() => session.GetGlobalSession());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportPath_ReleasesTrackedSessionBeforeReturningOrThrowing(bool invalidSource)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "SomeEngine-SlangLifetime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "lifetime.slang");
        File.WriteAllText(
            sourcePath,
            invalidSource
                ? "[shader(\"compute\")] void CSMain( {"
                : """
                  [shader("compute")]
                  [numthreads(1, 1, 1)]
                  void CSMain()
                  {
                  }
                  """);

        ISession? sessionProbe = null;
        IMetadata? metadataProbe = null;
        var trackedWrappers = new List<WeakReference>();
        try
        {
            Action<object> observe = value =>
            {
                trackedWrappers.Add(new WeakReference(value));
                if (value is ISession session)
                    sessionProbe = session;
                if (value is IMetadata metadata)
                    metadataProbe = metadata;
            };

            if (invalidSource)
            {
                Exception failure = Assert.ThrowsAny<Exception>(() =>
                    SlangShaderImporter.ImportTransientForLifetimeTest(sourcePath, observe));
                Assert.Contains("Failed", failure.Message, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                _ = SlangShaderImporter.ImportTransientForLifetimeTest(sourcePath, observe);
            }

            Assert.NotEmpty(trackedWrappers);
            AssertDisposedAndDrop(ref sessionProbe);
            if (invalidSource)
                Assert.Null(metadataProbe);
            else
                AssertDisposedAndDrop(ref metadataProbe);
            CollectFinalizers();
            Assert.All(trackedWrappers, reference => Assert.False(reference.IsAlive));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CollectFinalizers()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertDisposedAndDrop(ref ISession? sessionProbe)
    {
        ISession released = sessionProbe ??
            throw new InvalidOperationException("The importer did not expose its tracked session.");
        Assert.Throws<ObjectDisposedException>(() => released.GetGlobalSession());
        sessionProbe = null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertDisposedAndDrop(ref IMetadata? metadataProbe)
    {
        IMetadata released = metadataProbe ??
            throw new InvalidOperationException("The importer did not track compiled metadata.");
        Assert.Throws<ObjectDisposedException>(() =>
            released.IsParameterLocationUsed(
                SlangParameterCategory.DescriptorTableSlot,
                0,
                0,
                out _));
        metadataProbe = null;
    }

    private static MethodInfo Method(Type type, string name) =>
        Assert.Single(type.GetMethods(), method => method.Name == name);

    private static ParameterInfo Parameter(Type type, string methodName, int index) =>
        Method(type, methodName).GetParameters()[index];

    private static void AssertMetadataInterface(ParameterInfo parameter)
    {
        Assert.Equal(typeof(IMetadata).MakeByRefType(), parameter.ParameterType);
        Assert.Equal(
            NullabilityState.NotNull,
            new NullabilityInfoContext().Create(parameter).WriteState);
        MarshalUsingAttribute attribute = Assert.Single(
            parameter.GetCustomAttributes(typeof(MarshalUsingAttribute), inherit: false)
                .Cast<MarshalUsingAttribute>());
        Type nativeType = Assert.IsAssignableFrom<Type>(attribute.NativeType);
        Assert.True(nativeType.IsGenericType);
        Assert.Equal(
            typeof(UniqueComInterfaceMarshaller<>),
            nativeType.GetGenericTypeDefinition());
    }

    private static void AssertUniqueMarshaller(ParameterInfo parameter) =>
        AssertMarshaller(parameter, nameof(UniqueComInterfaceMarshaller<object>).Split('`')[0] + "`1");

    private static void AssertMarshaller(ParameterInfo parameter, string genericTypeName)
    {
        MarshalUsingAttribute attribute = Assert.Single(
            parameter.GetCustomAttributes(typeof(MarshalUsingAttribute), inherit: false)
                .Cast<MarshalUsingAttribute>());
        Type nativeType = Assert.IsAssignableFrom<Type>(attribute.NativeType);
        Assert.True(nativeType.IsGenericType);
        Assert.Equal(genericTypeName, nativeType.GetGenericTypeDefinition().Name);
    }
}
