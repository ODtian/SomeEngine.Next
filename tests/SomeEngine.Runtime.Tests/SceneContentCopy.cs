namespace SomeEngine.Runtime.Tests;

internal sealed class SceneContentCopy : IDisposable
{
    internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"SomeEngine-SceneCopy-{Guid.NewGuid():N}");

    internal SceneContentCopy(string sourceDirectory)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(DirectoryPath, Path.GetRelativePath(sourceDirectory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
        }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
    }
}
