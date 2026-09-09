using SomeEngine.Graphics;

namespace SomeEngine.Render.Cluster.Tests;

internal static class ClusterPublicationTestDriver
{
    internal static bool SubmitPending(this ClusterMeshes meshes)
    {
        ClusterMeshPublication? publication = meshes.PreparePublication();
        if (publication is null)
            return false;
        var retirements = new QueueCompletion[publication.Retirements.Length][];
        for (int index = 0; index < retirements.Length; index++)
            retirements[index] = [];
        meshes.CommitPublication(publication, retirements);
        return true;
    }
}
