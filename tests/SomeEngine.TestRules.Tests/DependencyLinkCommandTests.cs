using System.Collections.Concurrent;
using System.Diagnostics;
using System.Xml.Linq;

namespace SomeEngine.TestRules.Tests;

public sealed class DependencyLinkCommandTests
{
    [Fact]
    public async Task BuildReplacesAnEqualSizeAndTimestampDependencyCopyWithALink()
    {
        using var files = new TestProject();
        Directory.CreateDirectory(files.OutputDirectory);
        File.Copy(files.FirstBinary, files.OutputBinary);
        File.SetLastWriteTimeUtc(files.OutputBinary, File.GetLastWriteTimeUtc(files.FirstBinary));
        Assert.Null(new FileInfo(files.OutputBinary).LinkTarget);

        var build = await files.Run("build", files.Project, "-c", "Debug", "--nologo");

        Assert.True(build.ExitCode == 0, build.Output);
        Assert.NotNull(new FileInfo(files.OutputBinary).LinkTarget);
        Assert.Equal(files.FirstBinary, new FileInfo(files.OutputBinary).ResolveLinkTarget(true)!.FullName);
        Assert.Equal(File.ReadAllBytes(files.FirstBinary), File.ReadAllBytes(files.OutputBinary));
    }

    [Fact]
    public async Task BuildRetargetsADependencyWithEqualBytesAndTimestamp()
    {
        using var files = new TestProject();
        var first = await files.Run("build", files.Project, "-c", "Debug", "--nologo");
        Assert.True(first.ExitCode == 0, first.Output);
        Assert.Equal(files.FirstBinary, new FileInfo(files.OutputBinary).ResolveLinkTarget(true)!.FullName);
        Assert.Equal(File.ReadAllBytes(files.FirstBinary), File.ReadAllBytes(files.SecondBinary));
        Assert.Equal(File.GetLastWriteTimeUtc(files.FirstBinary), File.GetLastWriteTimeUtc(files.SecondBinary));

        var second = await files.Run("build", files.Project, "-c", "Debug", "--no-restore",
            "--nologo", "-p:DependencySource=second/dependency.dll");

        Assert.True(second.ExitCode == 0, second.Output);
        Assert.Equal(files.SecondBinary, new FileInfo(files.OutputBinary).ResolveLinkTarget(true)!.FullName);
    }

    [Fact]
    public async Task UnchangedBuildPreservesTheExistingDependencyLink()
    {
        using var files = new TestProject();
        var first = await files.Run("build", files.Project, "-c", "Debug", "--nologo");
        Assert.True(first.ExitCode == 0, first.Output);
        Assert.NotNull(new FileInfo(files.OutputBinary).LinkTarget);

        var changes = new ConcurrentQueue<WatcherChangeTypes>();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const string witness = "link-check-complete";
        using var watcher = new FileSystemWatcher(files.OutputDirectory)
        {
            NotifyFilter = NotifyFilters.FileName,
        };
        watcher.Created += (_, change) =>
        {
            if (change.Name == witness)
                delivered.TrySetResult();
            else if (change.Name == "dependency.dll")
                changes.Enqueue(change.ChangeType);
        };
        watcher.Deleted += (_, change) =>
        {
            if (change.Name == "dependency.dll")
                changes.Enqueue(change.ChangeType);
        };
        watcher.Renamed += (_, change) =>
        {
            if (change.Name == "dependency.dll" || change.OldName == "dependency.dll")
                changes.Enqueue(change.ChangeType);
        };
        watcher.Error += (_, error) => delivered.TrySetException(error.GetException());
        watcher.EnableRaisingEvents = true;

        var second = await files.Run("build", files.Project, "-c", "Debug", "--no-restore", "--nologo");
        Assert.True(second.ExitCode == 0, second.Output);
        // This later directory event acknowledges delivery of preceding link changes.
        File.WriteAllText(Path.Combine(files.OutputDirectory, witness), string.Empty);
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Empty(changes);
        Assert.Equal(files.FirstBinary, new FileInfo(files.OutputBinary).ResolveLinkTarget(true)!.FullName);
    }

    [Fact]
    public async Task DevelopmentSettingsRemainAnIndependentWritableFile()
    {
        using var files = new TestProject();
        var build = await files.Run("build", files.Project, "-c", "Debug", "--nologo");
        Assert.True(build.ExitCode == 0, build.Output);
        string settings = Path.Combine(files.OutputDirectory, "settings.ini");
        Assert.Null(new FileInfo(settings).LinkTarget);

        File.WriteAllText(settings, "changed=output");

        Assert.Equal("changed=output", File.ReadAllText(settings));
        Assert.Equal("original=source", File.ReadAllText(files.SourceSettings));
    }

    [Fact]
    public async Task VisualStudioBuildLinksDependencyBinaries()
    {
        using var files = new TestProject();

        var build = await files.Run("build", files.Project, "-c", "Debug",
            "--nologo", "-p:BuildingInsideVisualStudio=true");

        Assert.True(build.ExitCode == 0, build.Output);
        Assert.Equal(files.FirstBinary, new FileInfo(files.OutputBinary).ResolveLinkTarget(true)!.FullName);
    }

    [Fact]
    public async Task PublishWithoutBuildProducesAnIndependentRegularDependency()
    {
        using var files = new TestProject();
        var build = await files.Run("build", files.Project, "-c", "Debug", "--nologo");
        Assert.True(build.ExitCode == 0, build.Output);
        byte[] original = File.ReadAllBytes(files.FirstBinary);
        string publishDirectory = Path.Combine(files.Directory, "published");

        var publish = await files.Run("publish", files.Project, "-c", "Debug",
            "--no-build", "--no-restore", "--nologo", "-o", publishDirectory);

        Assert.True(publish.ExitCode == 0, publish.Output);
        string binary = Path.Combine(publishDirectory, "dependency.dll");
        Assert.Null(new FileInfo(binary).LinkTarget);
        Assert.Equal(original, File.ReadAllBytes(binary));
        File.WriteAllBytes(binary, [9, 8, 7]);
        Assert.Equal(original, File.ReadAllBytes(files.FirstBinary));
    }

    [Fact]
    public async Task OrdinaryContentOverridesACopyLocalReferenceWithoutChangingItsSource()
    {
        using var files = new TestProject();
        string libraryDirectory = Path.Combine(files.Directory, "library");
        Directory.CreateDirectory(libraryDirectory);
        string libraryProject = Path.Combine(libraryDirectory, "DependencyLibrary.csproj");
        File.WriteAllText(libraryProject, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        var libraryBuild = await files.Run("build", libraryProject, "-c", "Debug", "--nologo");
        Assert.True(libraryBuild.ExitCode == 0, libraryBuild.Output);
        string libraryAssembly = Path.Combine(libraryDirectory, "bin", "Debug", "net10.0", "DependencyLibrary.dll");
        byte[] libraryBytes = File.ReadAllBytes(libraryAssembly);

        string source = Path.Combine(files.Directory, "example.dll");
        File.WriteAllText(source, "ordinary content");
        var project = XDocument.Load(files.Project);
        project.Root!.Add(new XElement("ItemGroup",
            new XElement("ProjectReference", new XAttribute("Include", "library/DependencyLibrary.csproj")),
            new XElement("Compile", new XAttribute("Remove", "library/**/*.cs")),
            new XElement("Content",
                new XAttribute("Include", "example.dll"),
                new XAttribute("Link", "DependencyLibrary.dll"),
                new XAttribute("CopyToOutputDirectory", "PreserveNewest"))));
        project.Save(files.Project);

        var build = await files.Run("build", files.Project, "-c", "Debug", "--nologo");

        Assert.True(build.ExitCode == 0, build.Output);
        string output = Path.Combine(files.OutputDirectory, "DependencyLibrary.dll");
        Assert.Null(new FileInfo(output).LinkTarget);
        Assert.Equal("ordinary content", File.ReadAllText(output));
        Assert.Equal(libraryBytes, File.ReadAllBytes(libraryAssembly));
        File.WriteAllText(output, "changed output");
        Assert.Equal("ordinary content", File.ReadAllText(source));
        Assert.Equal(libraryBytes, File.ReadAllBytes(libraryAssembly));
    }

    [Fact]
    public async Task RemovingTheDependencyDeclarationRestoresIndependentWritableContent()
    {
        using var files = new TestProject();
        var first = await files.Run("build", files.Project, "-c", "Debug", "--nologo");
        Assert.True(first.ExitCode == 0, first.Output);
        Assert.NotNull(new FileInfo(files.OutputBinary).LinkTarget);
        byte[] original = File.ReadAllBytes(files.FirstBinary);
        var project = XDocument.Load(files.Project);
        project.Descendants("Content")
            .Single(item => item.Attribute("DependencyBinary")?.Value == "true")
            .Attribute("DependencyBinary")!.Remove();
        project.Save(files.Project);

        var second = await files.Run("build", files.Project, "-c", "Debug", "--no-restore", "--nologo");

        Assert.True(second.ExitCode == 0, second.Output);
        Assert.Null(new FileInfo(files.OutputBinary).LinkTarget);
        Assert.Equal(original, File.ReadAllBytes(files.OutputBinary));
        File.WriteAllBytes(files.OutputBinary, [4, 6, 8]);
        Assert.Equal(original, File.ReadAllBytes(files.FirstBinary));
    }

    [Fact]
    public async Task AReferencedProjectsDeclaredDependencyIsLinkedWithoutAFileExtension()
    {
        using var files = new TestProject();
        string libraryDirectory = Path.Combine(files.Directory, "library");
        Directory.CreateDirectory(libraryDirectory);
        string source = Path.Combine(libraryDirectory, "native-dependency");
        File.WriteAllBytes(source, [3, 7, 9]);
        File.WriteAllText(Path.Combine(libraryDirectory, "DependencyLibrary.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <Content Include="native-dependency" CopyToOutputDirectory="PreserveNewest" DependencyBinary="true" />
              </ItemGroup>
            </Project>
            """);
        var project = XDocument.Load(files.Project);
        project.Root!.Add(new XElement("ItemGroup",
            new XElement("ProjectReference", new XAttribute("Include", "library/DependencyLibrary.csproj")),
            new XElement("Compile", new XAttribute("Remove", "library/**/*.cs"))));
        project.Save(files.Project);

        var build = await files.Run("build", files.Project, "-c", "Debug", "--nologo");

        Assert.True(build.ExitCode == 0, build.Output);
        string output = Path.Combine(files.OutputDirectory, "native-dependency");
        Assert.NotNull(new FileInfo(output).LinkTarget);
        Assert.Equal(source, new FileInfo(output).ResolveLinkTarget(true)!.FullName);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(output));
    }

    [Fact]
    public async Task PublishWithoutBuildReplacesAnExistingDependencyLinkWithAnIndependentFile()
    {
        using var files = new TestProject();
        var build = await files.Run("build", files.Project, "-c", "Debug", "--nologo");
        Assert.True(build.ExitCode == 0, build.Output);
        byte[] original = File.ReadAllBytes(files.FirstBinary);
        string publishDirectory = Path.Combine(files.Directory, "published");
        Directory.CreateDirectory(publishDirectory);
        string binary = Path.Combine(publishDirectory, "dependency.dll");
        File.CreateSymbolicLink(binary, files.FirstBinary);
        Assert.NotNull(new FileInfo(binary).LinkTarget);

        var publish = await files.Run("publish", files.Project, "-c", "Debug",
            "--no-build", "--no-restore", "--nologo", "-o", publishDirectory);

        Assert.True(publish.ExitCode == 0, publish.Output);
        Assert.Null(new FileInfo(binary).LinkTarget);
        Assert.Equal(original, File.ReadAllBytes(binary));
        File.WriteAllBytes(binary, [4, 6, 8]);
        Assert.Equal(original, File.ReadAllBytes(files.FirstBinary));
    }

    private sealed class TestProject : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "SomeEngine.DependencyLinks-" + Guid.NewGuid().ToString("N"));
        public string Project => Path.Combine(Directory, "DependencyProbe.csproj");
        public string FirstBinary => Path.Combine(Directory, "first", "dependency.dll");
        public string SecondBinary => Path.Combine(Directory, "second", "dependency.dll");
        public string SourceSettings => Path.Combine(Directory, "settings.ini");
        public string OutputDirectory => Path.Combine(Directory, "bin", "Debug", "net10.0");
        public string OutputBinary => Path.Combine(OutputDirectory, "dependency.dll");

        public TestProject()
        {
            string repository = FindRepository();
            System.IO.Directory.CreateDirectory(Directory);
            File.Copy(Path.Combine(repository, "Directory.Build.targets"), Path.Combine(Directory, "Directory.Build.targets"));
            File.Copy(Path.Combine(repository, "global.json"), Path.Combine(Directory, "global.json"));
            File.WriteAllText(Project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                    <DependencySource Condition="'$(DependencySource)' == ''">first/dependency.dll</DependencySource>
                  </PropertyGroup>
                  <ItemGroup>
                    <Content Include="$(DependencySource)" Link="dependency.dll" CopyToOutputDirectory="PreserveNewest" DependencyBinary="true" />
                    <Content Include="settings.ini" CopyToOutputDirectory="PreserveNewest" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(Directory, "Main.cs"), "System.Console.WriteLine(\"dependency probe\");");
            foreach (string binary in new[] { FirstBinary, SecondBinary })
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
                File.WriteAllBytes(binary, [1, 3, 5, 7, 9]);
                File.SetLastWriteTimeUtc(binary, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            }
            File.WriteAllText(SourceSettings, "original=source");
        }

        public async Task<(int ExitCode, string Output)> Run(params string[] arguments)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = Directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(limit.Token);
                return (process.ExitCode, await output + await errors);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }

        private static string FindRepository()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "SomeEngine.slnx")))
                    return directory.FullName;
            throw new DirectoryNotFoundException("SomeEngine.slnx");
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
