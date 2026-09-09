using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.Marshalling;
using SlangShaderSharp;
using SomeEngine.Assets.Importers;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class SlangComLifetimeTests
{



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





}
