{
  "Format": "SomeEngine.AssetSource.2",
  "TypeId": "59349096-5d2a-109e-f2f0-d4600074e76a",
  "Schema": "D653E233B7DA482F",
  "Fields": {
    "Id": "6a0b073b-73a8-4ecd-9f4d-6a46129615ec",
    "Name": "Physics crowd skin",
    "AssetType": "SomeEngine.Assets.Schema.Shader"
  },
  "Blocks": [
    {
      "TypeId": "def46de0-7af8-87e0-75b9-660aaa202b94",
      "Schema": "163D4CAD0FD3CEDB",
      "Fields": {
        "SourceNames": [
          "cluster_transparency.slang",
          "physics-crowd.slang"
        ],
        "Inputs": [],
        "Specializations": [
          {
            "Arguments": [
              "0"
            ],
            "EntryPoint": "ClusterShadeMode"
          },
          {
            "Arguments": [
              "0",
              "0"
            ],
            "EntryPoint": "ClusterShadowReceiver"
          },
          {
            "Arguments": [
              "0",
              "0",
              "false"
            ],
            "EntryPoint": "ClusterForwardShade"
          },
          {
            "Arguments": [
              "0",
              "0",
              "true"
            ],
            "EntryPoint": "ClusterForwardShade"
          },
          {
            "Arguments": [
              "8"
            ],
            "EntryPoint": "ClusterShadeMode"
          },
          {
            "Arguments": [
              "0",
              "8"
            ],
            "EntryPoint": "ClusterShadowReceiver"
          },
          {
            "Arguments": [
              "0",
              "8",
              "false"
            ],
            "EntryPoint": "ClusterForwardShade"
          },
          {
            "Arguments": [
              "0",
              "8",
              "true"
            ],
            "EntryPoint": "ClusterForwardShade"
          }
        ]
      },
      "Chunks": {}
    }
  ]
}