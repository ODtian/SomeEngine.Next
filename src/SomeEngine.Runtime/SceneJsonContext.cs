using System.Text.Json.Serialization;

namespace SomeEngine.Runtime;

[JsonSerializable(typeof(TestScene[]))]
[JsonSerializable(typeof(ApplicationStartupTimes))]
[JsonSerializable(typeof(List<SceneTestPlatform.SceneTiming>))]
[JsonSerializable(typeof(List<SceneTestPlatform.SceneFrameWindow>))]
internal partial class SceneJsonContext : JsonSerializerContext;
