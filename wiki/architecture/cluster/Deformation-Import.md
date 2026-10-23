# Cluster Deformation Import

## Vertex correspondence

The Cluster importer set consumes the public `VertexStreamsBlock` and the selected `ClusterGeometryBlock`. `geometry.Vertices` describes each welded vertex with a source primitive, expanded stream row and original accessor row. Each `geometry.Clusters[i].Vertices[j]` selects the welded vertex represented by cluster-local vertex `j`.

Deformation Blocks carry the same `VertexIdentity` and use that cluster-local ordering. Values come from the expanded stream row, so prepared corner duplication and geometry welding retain their matching influences and deltas. The original accessor row remains available through the geometry mapping.

## Skin influences

`ClusterSkinInfluencesImporter` produces `ClusterSkinInfluencesBlock` on the mesh asset when source influence streams are present. Each `Clusters[i]` exposes:

| Member | Content |
| --- | --- |
| `Attributes` | All source `JOINTS_n` and `WEIGHTS_n` streams, with their semantic names and values |
| `Data` | Opaque bytes for the selected material encoding |

Each `ClusterVertexAttribute` stores its maximum `ComponentCount`, each vertex's `VertexComponentCounts`, and exact little-endian float64 values. `ReadComponent(vertex, component)` reads those values after asset save/open. A zero component count identifies an attribute omitted by that source primitive. Influence set numbers and counts follow the source. Joint values address the source skin's joint order; the shared `SkinBinding.JointBones` maps those joints to bones.

The default encoding is `someengine.cluster.skin.float64.v1`. It uses the existing attribute stream descriptor layout and float64 values in `Data`. The source records and encoded bytes are both persisted. A project-selected importer can supply its own `Encoding` and `Data` while consuming the same public source streams and geometry mapping.

## Morph deltas

`ClusterMorphDeltasImporter` produces `ClusterMorphDeltasBlock` on meshes with morph targets. `Clusters[i].Targets[target]` retains the source target order. Each target exposes all of its named `Attributes` and opaque `Data` in the same cluster-local vertex order. Position, normal, tangent, UV, color and custom streams follow the same component mapping.

`Clusters[i].SourceTangents[target]` retains original source tangent deltas when vertex preparation generated a replacement tangent basis. The entry is null for a target with no retained source tangent stream. These records follow the same vertex mapping as the target's current tangent deltas and survive asset save/open.

The default encoding is `someengine.cluster.morph.float64.v1`; each target's `Data` uses the stream descriptor layout with full float64 values. Target names and default weights remain in the neutral `MorphTargetSetBlock`. Both deformation importers belong to the Cluster importer set, and optional Block presence follows the source features.

## Material input preparation

The renderer's mesh builder orders clusters into LOD pages and splits material regions into draw records. A concrete material importer can combine shading bytes with the deformation bytes for each geometry cluster before that packing step. Its selected `ClusterVertexAttributesBlock` then carries the composed opaque bytes through the existing attribute view. The material's shader defines their interpretation.

Sources: [ClusterSkinInfluencesBlock](../../../src/SomeEngine.Assets/Schema/ClusterSkinInfluencesBlock.cs), [ClusterMorphDeltasBlock](../../../src/SomeEngine.Assets/Schema/ClusterMorphDeltasBlock.cs), [ClusterSkinInfluencesImporter](../../../src/SomeEngine.Assets.Importers/Importers/ClusterSkinInfluencesImporter.cs), [ClusterMorphDeltasImporter](../../../src/SomeEngine.Assets.Importers/Importers/ClusterMorphDeltasImporter.cs), [ClusterMeshData](../../../src/SomeEngine.Assets/Pipeline/ClusterMeshData.cs).

## Relations

[[architecture/Assets/Block-Division|Block Division]] · [[architecture/Assets/Gltf-Mesh-Import|glTF Mesh Import]] · [[architecture/cluster/Vertex-Encoding|Vertex Encoding]] · #106
