using SomeEngine.Runtime;

namespace SomeEngine.Runtime.Tests;

public sealed class SceneSelectionTests
{
    [Fact]
    public void NamedSceneRetainsContentAnimationAndMeasurementFrames()
    {
        string path = Path.GetTempFileName();
        try
        {
            string content = """
                [{"Name":"1024 spheres","ContentDirectory":".","Animate":true,
                  "MeasurementFrames":{"FirstFrame":2048,"FrameCount":1024}},
                 {"Name":"Sponza","ContentDirectory":"imported/Sponza"}]
                """;
            File.WriteAllText(path, content);

            TestScene scene = Assert.Single(SceneTestPlatform.ReadScenes(path, "1024 SPHERES"));

            Assert.Equal("1024 spheres", scene.Name);
            Assert.Equal(".", scene.ContentDirectory);
            Assert.True(scene.Animate);
            SimulationFrameWindow frames = Assert.IsType<SimulationFrameWindow>(scene.MeasurementFrames);
            Assert.Equal(2048, frames.FirstFrame);
            Assert.Equal(1024, frames.FrameCount);
            Assert.Equal(content, File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingSceneNameIsRejected()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """[{"Name":"Sponza","ContentDirectory":"."}]""");

            var error = Assert.Throws<ArgumentException>(() => SceneTestPlatform.ReadScenes(path, "Amazon Bistro"));

            Assert.Equal("sceneName", error.ParamName);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DuplicateSceneNamesAreRejected()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                [{"Name":"Sponza","ContentDirectory":"first"},
                 {"Name":"sponza","ContentDirectory":"second"}]
                """);

            var error = Assert.Throws<ArgumentException>(() => SceneTestPlatform.ReadScenes(path, "Sponza"));

            Assert.Equal("sceneName", error.ParamName);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NoSceneSelectionRetainsConfiguredOrder()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                [{"Name":"1024 spheres","ContentDirectory":"."},
                 {"Name":"Sponza","ContentDirectory":"imported/Sponza"},
                 {"Name":"Amazon Bistro","ContentDirectory":"imported/Bistro"}]
                """);

            TestScene[] scenes = SceneTestPlatform.ReadScenes(path);

            Assert.Equal(new[] { "1024 spheres", "Sponza", "Amazon Bistro" }, scenes.Select(scene => scene.Name));
        }
        finally { File.Delete(path); }
    }
}
