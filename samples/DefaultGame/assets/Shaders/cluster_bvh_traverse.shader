{
  "Format": "SomeEngine.AssetSource.2",
  "TypeId": "59349096-5d2a-109e-f2f0-d4600074e76a",
  "Schema": "D653E233B7DA482F",
  "Fields": {
    "Id": "66c64f54-9883-59f4-aabf-d83f3925b7df",
    "Name": "cluster_bvh_traverse",
    "AssetType": "SomeEngine.Assets.Schema.Shader"
  },
  "Blocks": [
    {
      "TypeId": "def46de0-7af8-87e0-75b9-660aaa202b94",
      "Schema": "163D4CAD0FD3CEDB",
      "Fields": {
        "Inputs": [],
        "SourceNames": [
          "Pipeline/build_cluster_bvh_traverse.slang"
        ],
        "Specializations": [
          {
            "EntryPoint": "CullClusterWork",
            "Arguments": [
              "false",
              "false"
            ]
          },
          {
            "EntryPoint": "CullClusterWork",
            "Arguments": [
              "false",
              "true"
            ]
          },
          {
            "EntryPoint": "CullClusterWork",
            "Arguments": [
              "true",
              "false"
            ]
          },
          {
            "EntryPoint": "CullClusterWork",
            "Arguments": [
              "true",
              "true"
            ]
          }
        ]
      },
      "Chunks": {}
    }
  ]
}
