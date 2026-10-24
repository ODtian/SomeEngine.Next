using System.Text.Json.Serialization;
using SomeEngine.Runtime;

namespace DefaultGame;

[JsonSerializable(typeof(TestScene[]))]
[JsonSerializable(typeof(ApplicationStartupTimes))]
[JsonSerializable(typeof(List<SceneTestPlatform.SceneTiming>))]
internal partial class SceneJsonContext : JsonSerializerContext;
