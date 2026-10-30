using System.Text.Json.Serialization;
using SomeEngine.Runtime;

namespace DefaultGame;

[JsonSerializable(typeof(TestScene[]))]
[JsonSerializable(typeof(ApplicationStartupTimes))]
[JsonSerializable(typeof(List<SceneTestPlatform.SceneTiming>))]
[JsonSerializable(typeof(List<SceneTestPlatform.SceneFrameWindow>))]
[JsonSerializable(typeof(List<SceneColorTimeReport>))]
[JsonSerializable(typeof(SceneColorTimeConfiguration))]
[JsonSerializable(typeof(SceneColorTimeProgress))]
[JsonSerializable(typeof(SceneColorTimeOrder))]
internal partial class SceneJsonContext : JsonSerializerContext;
