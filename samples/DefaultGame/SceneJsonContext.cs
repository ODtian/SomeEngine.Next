using System.Text.Json.Serialization;
using SomeEngine.Runtime;

namespace DefaultGame;

[JsonSerializable(typeof(TestScene[]))]
[JsonSerializable(typeof(ApplicationStartupTimes))]
[JsonSerializable(typeof(List<SceneTestPlatform.SceneTiming>))]
[JsonSerializable(typeof(List<SceneTestPlatform.SceneFrameWindow>))]
internal partial class SceneJsonContext : JsonSerializerContext;
