using SomeEngine.Tests;

namespace SomeEngine.Render.Cluster.Tests;

// Content is disposable scratch; image/raw/CSV failure evidence is not.
internal sealed class ShadowEvidence : IDisposable
{
    private readonly bool _retainSuccess;
    private bool _complete;
    internal string Directory { get; }
    internal string Content => Path.Combine(Directory, "content");

    internal ShadowEvidence(string name)
    {
        string? requested = Environment.GetEnvironmentVariable("SOMEENGINE_SHADOW_EVIDENCE");
        _retainSuccess = requested is not null;
        Directory = Path.Combine(requested ?? Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(Directory);
    }

    internal void Complete() => _complete = true;

    public void Dispose()
    {
        if (System.IO.Directory.Exists(Content)) System.IO.Directory.Delete(Content, recursive: true);
        if (!_complete)
        {
            foreach (string comparison in System.IO.Directory.EnumerateFiles(Directory, "*error.png", SearchOption.AllDirectories))
            {
                string parent = Path.GetDirectoryName(comparison)!;
                string expected = File.Exists(Path.Combine(parent, "rt-shadow.png")) ? "rt-shadow.png" : "triangle-reference.png";
                string actual = File.Exists(Path.Combine(parent, "rendered-shadow.png")) ? "rendered-shadow.png" : "rendered-visibility.png";
                if (File.Exists(Path.Combine(parent, expected))) File.Copy(Path.Combine(parent, expected), Path.Combine(parent, "expected.png"), true);
                if (File.Exists(Path.Combine(parent, actual))) File.Copy(Path.Combine(parent, actual), Path.Combine(parent, "actual.png"), true);
                File.Copy(comparison, Path.Combine(parent, "diff.png"), true);
            }
            string destination = Path.Combine(ImageArtifacts.Root, "images", Path.GetFileName(Directory));
            if (Path.GetFullPath(destination) != Path.GetFullPath(Directory))
            {
                foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.AllDirectories))
                {
                    string target = Path.Combine(destination, Path.GetRelativePath(Directory, file));
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, true);
                }
            }
            Console.Error.WriteLine($"Shadow assertion artifacts: {Path.GetFullPath(destination)}");
        }
        if (!_retainSuccess) System.IO.Directory.Delete(Directory, recursive: true);
    }
}
