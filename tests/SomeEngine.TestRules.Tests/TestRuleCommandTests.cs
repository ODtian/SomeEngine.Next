using System.Diagnostics;

namespace SomeEngine.TestRules.Tests;

public class TestRuleCommandTests
{
    [Theory]
    [InlineData("TEST001", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"Example.Tests\")]", false)]
    [InlineData("TEST002", "class Example { void Rejects() { Assert.Equal(\"fault\", error.Message); } }", false)]
    [InlineData("TEST003", "class Example { void Waits() { System.Threading.Thread.Sleep(1); } }", false)]
    [InlineData("TEST001", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"Example.Tests\")]", true)]
    [InlineData("TEST002", "class Example { void Rejects() { Assert.Equal(\"fault\", error.Message); } }", true)]
    [InlineData("TEST003", "class Example { void Waits() { System.Threading.Thread.Sleep(1); } }", true)]
    public async Task DefaultTestCommandReportsViolations(string rule, string source, bool noBuild)
    {
        using var files = new TestRepository();
        files.Write("tests/Example.Tests/Behavior.cs", source);

        var result = await files.Run(noBuild ? new[] { "test", files.Project, "--no-build" } : new[] { "test", files.Project });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("error " + rule, result.Output);
        Assert.Contains("Behavior.cs(1,", result.Output);
        Assert.Contains("Behavior-Checks.md", result.Output);
    }

    [Fact]
    public async Task DefaultTestCommandReportsBuildFileAccessToTests()
    {
        using var files = new TestRepository();
        files.Write("Library.props", "<Project><ItemGroup><InternalsVisibleTo Include=\"Example.Tests\" /></ItemGroup></Project>");

        var result = await files.Run("test", files.Project, "--no-build");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Library.props(1,", result.Output);
        Assert.Contains("error TEST001", result.Output);
    }

    [Theory]
    [InlineData("Example.Tests")]
    [InlineData("SomeEngine.Graphics.Vulkan.Tests")]
    [InlineData("SomeEngine.ECS.Fuzz.Tests")]
    [InlineData("Example.Benchmarks.Tests")]
    public async Task CheckCommandReportsEveryViolationAcrossTestCategories(string projectName)
    {
        using var files = new TestRepository(projectName);
        files.Write($"tests/{projectName}/Behavior.cs", "class Example { void Waits() { Thread.Sleep(1); Thread.Sleep(1); } }");

        var result = await files.Run("msbuild", files.Project, "-t:CheckTestRules", "-v:minimal");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Behavior.cs(1,32): error TEST003", result.Output);
        Assert.Contains("Behavior.cs(1,49): error TEST003", result.Output);
        Assert.Contains($"Test rules: {projectName}; 2 violations.", result.Output);
    }

    [Fact]
    public async Task CheckCommandAcceptsPublicBehaviorTests()
    {
        using var files = new TestRepository();
        files.Write("tests/Example.Tests/Behavior.cs", "class Example { void Rejects() { Assert.Throws<ArgumentException>(Run); Assert.Equal(\"amount\", error.ParamName); } }");

        var result = await files.Run("msbuild", files.Project, "-t:CheckTestRules", "-v:minimal");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Test rules: Example.Tests; 0 violations.", result.Output);
    }

    [Fact]
    public async Task RepositoryCheckReportsViolationsInEveryTestProject()
    {
        using var files = new TestRepository();
        files.Write("tests/Example.Tests/Behavior.cs", "class Example { void Waits() { Thread.Sleep(1); } }");
        foreach (var projectName in new[] { "SomeEngine.Graphics.Vulkan.Tests", "Example.Benchmarks.Tests" })
        {
            files.Write($"tests/{projectName}/{projectName}.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
            files.Write($"tests/{projectName}/Behavior.cs", "class Example { void Waits() { Thread.Sleep(1); } }");
        }

        var result = await files.RunPowerShell("-NoProfile", "-File", "tools/Check-TestRules.ps1");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Test rules: Example.Tests; 1 violations.", result.Output);
        Assert.Contains("Test rules: SomeEngine.Graphics.Vulkan.Tests; 1 violations.", result.Output);
        Assert.Contains("Test rules: Example.Benchmarks.Tests; 1 violations.", result.Output);
        Assert.Contains("Test rules checked in 3 test projects.", result.Output);
    }

    private sealed class TestRepository : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "SomeEngine.TestRules-" + Guid.NewGuid().ToString("N"));
        public string Project { get; }

        public TestRepository(string projectName = "Example.Tests")
        {
            Project = Path.Combine(directory, "tests", projectName, projectName + ".csproj");
            var repository = FindRepository();
            foreach (var relative in new[] { "Directory.Build.props", "Directory.Build.targets", "tools/Check-TestRules.ps1", "tools/TestRules/CheckTestRules.cs", "tools/TestRules/SomeEngine.TestRules.csproj" })
                Write(relative, File.ReadAllText(Path.Combine(repository, relative)));
            Write($"tests/{projectName}/{projectName}.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
        }

        public void Write(string relative, string text)
        {
            var path = Path.Combine(directory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public Task<(int ExitCode, string Output)> Run(params string[] arguments) =>
            RunProcess(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", arguments);

        public Task<(int ExitCode, string Output)> RunPowerShell(params string[] arguments) =>
            RunProcess("pwsh", arguments);

        private async Task<(int ExitCode, string Output)> RunProcess(string executable, string[] arguments)
        {
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
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

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
