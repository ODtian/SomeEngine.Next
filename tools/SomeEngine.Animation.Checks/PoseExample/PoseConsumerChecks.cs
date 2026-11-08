using System.Numerics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SomeEngine.Animation.Pose;
using SomeEngine.Assets;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Animation.Checks.PoseExample;

internal static class PoseConsumerChecks
{
    internal static async Task<AnimationClip> RunAsync(string directory)
    {
        AnimationClip clip = await ImportAsync(directory);
        var values = new BoneTransform[clip.Skeleton.BoneCount];
        clip.SampleLocal(.25f, values);
        Check(Vector3.Distance(new(.1f, 0, 0), values[0].Translation) < .00001f,
            "The reopened consumer codec must apply the imported own-bone velocity.");
        PoseConsumerFlow.CheckPlayback(clip);
        Console.WriteLine("POSE_CONSUMER codec=ok importer=ok ready-after-close=ok");
        return clip;
    }

    internal static async Task<AnimationClip> ImportAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var project = new AssetProject(directory, []);
        ConsumerPoseSource source = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "PoseExample", "motion.json")),
            PoseExampleJson.Default.ConsumerPoseSource)!;
        var reference = new BoneTransform { Rotation = Quaternion.Identity, Scale = Vector3.One };
        using (var skeletonAsset = new SkeletonAsset(source.Skeleton) { Name = "Consumer bones" })
        {
            skeletonAsset.SetBlock(new SkeletonBlock([-1, 0], [reference, reference with { Translation = Vector3.UnitY }], [1, 2]));
            project.RegisterAsset(await skeletonAsset.SaveAsync(Path.Combine(directory, "consumer.skeleton.asset")));
        }
        var services = new ServiceCollection();
        PoseExampleCapabilities.AddAssetCapabilities(services);
        await using ServiceProvider provider = services.BuildServiceProvider();
        AssetImporterCapability capability = provider.GetServices<AssetImporterCapability>()
            .Single(value => value.Id == ConsumerPoseImporter.Id);
        var metadata = new SourceMeta
        {
            SourceGuid = new(new Guid("7958cc63-0f8e-40a6-b161-8ad77da18951")), UseDefaultImporters = false,
        };
        metadata.Importers.Add(new(ConsumerPoseImporter.Id, default));
        metadata.ElementIds.Add("clip", new Guid("2e65c537-9f86-4e8f-9d16-7cda99315e1f"));
        string sourcePath = Path.Combine(directory, "motion.json");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "PoseExample", "motion.json"), sourcePath, overwrite: true);
        SourceMeta.Save(sourcePath, metadata);
        string clipPath = Path.Combine(directory, "consumer.animation-clip.asset");
        await using (var context = new ImportContext(directory, "motion.json", metadata))
        {
            context.EnableImporter<ConsumerPoseSource, AnimationClipAsset>(capability.Bind<ConsumerPoseSource>(provider));
            context.AddAsset(source, new AnimationClipAsset(context.GetAssetId("clip")) { Name = "Consumer motion" }, "clip", clipPath);
            AnimationClipAsset imported = await context.GetAssetAsync<ConsumerPoseSource, AnimationClipAsset>(source);
            project.RegisterAsset(await imported.SaveAsync(clipPath));
        }
        using var skeletonFile = await SkeletonAsset.OpenAsync(Path.Combine(directory, "consumer.skeleton.asset"));
        Skeleton own = (await skeletonFile.GetBlockAsync<SkeletonBlock>()).CreateSkeleton();
        var parent = new Skeleton([-1], [reference], [10]);
        var skeleton = new Skeleton(parent, own.Parents, own.ReferencePose, own.NameHashes, inheritedParents: [0, -1]);
        using var clipFile = await AnimationClipAsset.OpenAsync(clipPath);
        return (await clipFile.GetBlockAsync<ConsumerPoseBlock>()).CreateClip(skeleton);
    }

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
