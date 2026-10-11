using System.Text.Json.Serialization;

namespace SomeEngine.Runtime;

[JsonSerializable(typeof(TestScene[]))]
[JsonSerializable(typeof(ApplicationStartupTimes))]
[JsonSerializable(typeof(List<SceneTestPlatform.SceneTiming>))]
internal partial class SceneJsonContext : JsonSerializerContext;
