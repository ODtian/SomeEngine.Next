using System.Diagnostics;
using SomeEngine.TestRules;

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
    public async Task DefaultTestCommandReportsNewViolations(string rule, string source, bool noBuild)
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

    [Fact]
    public async Task CheckCommandAcceptsWhitelistedViolations()
    {
        using var files = new TestRepository();
        const string source = "class Example { void Waits() { Thread.Sleep(1); } }";
        files.Write("tests/Example.Tests/Behavior.cs", source);
        var violation = Assert.Single(TestRuleScanner.ReadCSharp("tests/Example.Tests/Behavior.cs", source, true, "Example.Tests"));
        files.Write("tools/TestRules/TestRuleWhitelist.txt", "[Example.Tests]\n" + violation.WhitelistEntry);

        var result = await files.Run("msbuild", files.Project, "-t:CheckTestRules", "-v:minimal");

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task CheckCommandAcceptsPublicBehaviorTests()
    {
        using var files = new TestRepository();
        files.Write("tests/Example.Tests/Behavior.cs", "class Example { void Rejects() { Assert.Throws<ArgumentException>(Run); Assert.Equal(\"amount\", error.ParamName); } }");

        var result = await files.Run("msbuild", files.Project, "-t:CheckTestRules", "-v:minimal");

        Assert.Equal(0, result.ExitCode);
    }

    private sealed class TestRepository : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "SomeEngine.TestRules-" + Guid.NewGuid().ToString("N"));
        public string Project => Path.Combine(directory, "tests", "Example.Tests", "Example.Tests.csproj");

        public TestRepository()
        {
            var repository = FindRepository();
            foreach (var relative in new[] { "Directory.Build.props", "Directory.Build.targets", "tools/TestRules/CheckTestRules.cs", "tools/TestRules/SomeEngine.TestRules.csproj" })
                Write(relative, File.ReadAllText(Path.Combine(repository, relative)));
            Write("tools/TestRules/TestRuleWhitelist.txt", "");
            Write("tests/Example.Tests/Example.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
        }

        public void Write(string relative, string text)
        {
            var path = Path.Combine(directory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public async Task<(int ExitCode, string Output)> Run(params string[] arguments)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
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
