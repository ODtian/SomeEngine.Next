using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.Marshalling;
using SlangShaderSharp;
using SomeEngine.Assets.Importers;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class SlangComLifetimeTests
{
    [Fact]
    public void ImporterOwnedResultsDeclareUniqueComMarshalling()
    {
        MethodInfo createBlob = typeof(Slang)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method =>
                method.Name == "CreateBlob" &&
                method.GetParameters() is [{ ParameterType.IsPointer: true }, _]);
        AssertUnique(createBlob.ReturnParameter);
        AssertUnique(Parameter(typeof(IGlobalSession), nameof(IGlobalSession.CreateSession), 1));

        MethodInfo load = Method(typeof(ISession), nameof(ISession.LoadModuleFromSource));
        AssertUnique(load.ReturnParameter);
        AssertUnique(load.GetParameters()[3]);
        AssertUnique(Parameter(typeof(ISession), nameof(ISession.CreateCompositeComponentType), 2));
        AssertUnique(Parameter(typeof(ISession), nameof(ISession.CreateCompositeComponentType), 3));
        AssertUnique(Parameter(typeof(IModule), nameof(IModule.GetDefinedEntryPoint), 1));
        AssertUnique(Parameter(typeof(IComponentType), nameof(IComponentType.GetLayout), 1));
        AssertUnique(Parameter(typeof(IComponentType), nameof(IComponentType.Link), 0));
        AssertUnique(Parameter(typeof(IComponentType), nameof(IComponentType.Link), 1));
        AssertUnique(Parameter(typeof(IComponentType), nameof(IComponentType.GetEntryPointCode), 2));
        AssertUnique(Parameter(typeof(IComponentType), nameof(IComponentType.GetEntryPointCode), 3));
        AssertMetadataMarshaller(
            Parameter(typeof(IComponentType), nameof(IComponentType.GetTargetMetadata), 1));
        AssertMetadataMarshaller(
            Parameter(typeof(IComponentType), nameof(IComponentType.GetEntryPointMetadata), 2));
        AssertMetadataMarshaller(
            Parameter(typeof(ICompileResult), nameof(ICompileResult.GetMetadata), 0));
    }

    [Fact]
    public void CreateBlob_UsesUniqueWrapperAndFinalReleaseInvalidatesIt()
    {
        ISlangBlob blob = Slang.CreateBlob([0x5A]);
        ComObject wrapper = Assert.IsAssignableFrom<ComObject>(blob);

        wrapper.FinalRelease();

        Assert.Throws<ObjectDisposedException>(() => blob.GetBufferSize());
    }

    [Fact]
    public void TrackedSession_IsInvalidAfterLifetimeDispose()
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
        Metadata? metadataProbe = null;
        var trackedWrappers = new List<WeakReference>();
        try
        {
            Action<object> observe = value =>
            {
                trackedWrappers.Add(new WeakReference(value));
                if (value is ISession session)
                    sessionProbe = session;
                if (value is Metadata metadata)
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
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            Assert.All(trackedWrappers, reference => Assert.False(reference.IsAlive));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
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
    private static void AssertDisposedAndDrop(ref Metadata? metadataProbe)
    {
        Metadata released = metadataProbe ??
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

    private static void AssertUnique(ICustomAttributeProvider provider)
    {
        MarshalUsingAttribute attribute = Assert.Single(
            provider.GetCustomAttributes(typeof(MarshalUsingAttribute), inherit: false)
                .Cast<MarshalUsingAttribute>());
        Type nativeType = Assert.IsAssignableFrom<Type>(attribute.NativeType);
        Assert.True(nativeType.IsGenericType);
        Assert.Equal(
            typeof(UniqueComInterfaceMarshaller<>),
            nativeType.GetGenericTypeDefinition());
    }

    private static void AssertMetadataMarshaller(ParameterInfo parameter)
    {
        Assert.Equal(typeof(Metadata).MakeByRefType(), parameter.ParameterType);
        Assert.Equal(
            NullabilityState.Nullable,
            new NullabilityInfoContext().Create(parameter).WriteState);
        MarshalUsingAttribute attribute = Assert.Single(
            parameter.GetCustomAttributes(typeof(MarshalUsingAttribute), inherit: false)
                .Cast<MarshalUsingAttribute>());
        Type nativeType = Assert.IsAssignableFrom<Type>(attribute.NativeType);
        Assert.Equal("SlangShaderSharp.MetadataMarshaller", nativeType.FullName);
    }
}
