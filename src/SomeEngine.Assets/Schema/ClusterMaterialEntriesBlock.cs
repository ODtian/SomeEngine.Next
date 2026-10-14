namespace SomeEngine.Assets.Schema;

/// <summary>The Cluster pipeline's Slang entry IR, selected within a shared shader asset.</summary>
[AssetBlock("SomeEngine.Assets.Schema.ClusterMaterialEntriesBlock")]
public sealed partial class ClusterMaterialEntriesBlock : ShaderInputBlock
{
    public static ClusterMaterialEntriesBlock Create()
    {
        var result = new ClusterMaterialEntriesBlock { SourceNames = ["cluster_material.slang"] };
        result.SetBytes(SerializedSourceKey(0), StandardMaterialInputs.Read("cluster_material"));
        return result;
    }

    public static ClusterMaterialEntriesBlock CreateUnlit()
    {
        var result = new ClusterMaterialEntriesBlock { SourceNames = ["cluster_unlit_material.slang"] };
        result.SetSerializedSource(0, StandardMaterialInputs.Read("cluster_unlit_material"));
        return result;
    }
}
