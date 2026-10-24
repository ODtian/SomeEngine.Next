using System.Diagnostics;
using System.Security;
using SkiaSharp;

namespace SomeEngine.Runtime.Tests;

public sealed class SceneImageArtifactsTests
{
    [Fact]
    public async Task FailedImageAssertionsReportFailureAndSaveActualExpectedAndDifferenceImages()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-ImageAssertions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (!File.Exists(Path.Combine(repository.FullName, "SomeEngine.slnx"))) repository = repository.Parent!;
            File.Copy(Path.Combine(repository.FullName, "tests", "SomeEngine.Runtime.Tests", "SceneImages.cs"), Path.Combine(directory, "SceneImages.cs"));
            File.WriteAllText(Path.Combine(directory, "ImageAssertions.csproj"), $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                    <IsTestProject>true</IsTestProject>
                  </PropertyGroup>
                  <ItemGroup>
                    <Reference Include="SomeEngine.Runtime"><HintPath>{{SecurityElement.Escape(typeof(SceneImage).Assembly.Location)}}</HintPath></Reference>
                    <Reference Include="SomeEngine.Resources"><HintPath>{{SecurityElement.Escape(typeof(SomeEngine.Resources.Material).Assembly.Location)}}</HintPath></Reference>
                    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                    <PackageReference Include="xunit" Version="2.9.3" />
                    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                    <PackageReference Include="SkiaSharp" Version="3.119.4" />
                    <Using Include="Xunit" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(directory, "Failures.cs"), """
                namespace SomeEngine.Runtime.Tests;
                public class ImageAssertions
                {
                    [Fact]
                    public void WholeImage()
                    {
                        SceneImages.AssertEqual(new(0, 2, 1, new byte[] { 10, 20, 30, 255, 10, 20, 30, 255 }),
                            new(1, 2, 1, new byte[] { 60, 20, 30, 255, 10, 20, 30, 255 }));
                    }
                    [Fact]
                    public void Region()
                    {
                        SceneImages.AssertEqualRegion(new(0, 2, 1, new byte[] { 10, 20, 30, 255, 10, 20, 30, 255 }),
                            new(1, 2, 1, new byte[] { 10, 20, 30, 255, 60, 20, 30, 255 }), 1, 0, 1, 1);
                    }
                    [Fact]
                    public void Color()
                    {
                        SceneImages.AssertColorRegion(new(1, 2, 1, new byte[] { 4, 5, 6, 255, 4, 5, 6, 255 }),
                            1, 0, 1, 1, 100, 110, 120);
                    }
                    [Fact]
                    public void Size()
                    {
                        SceneImages.AssertEqual(new(0, 1, 1, new byte[] { 1, 2, 3, 255 }),
                            new(1, 2, 1, new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }));
                    }
                    [Fact]
                    public void EqualImages()
                    {
                        var image = new SceneImage(0, 1, 1, new byte[] { 1, 2, 3, 255 });
                        SceneImages.AssertEqual(image, image);
                        SceneImages.AssertEqualRegion(image, image, 0, 0, 1, 1);
                        SceneImages.AssertColorRegion(image, 0, 0, 1, 1, 1, 2, 3);
                    }
                }
                """);
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("test");
            start.ArgumentList.Add("ImageAssertions.csproj");
            start.ArgumentList.Add("-v:quiet");
            string artifacts = Path.Combine(directory, "artifacts");
            start.Environment["SOMEENGINE_TEST_ARTIFACTS"] = artifacts;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(limit.Token); }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            string diagnostics = await output + await errors;
            Assert.NotEqual(0, process.ExitCode);
            Assert.True(Directory.Exists(Path.Combine(artifacts, "images")), diagnostics);
            string[] images = Directory.GetDirectories(Path.Combine(artifacts, "images"));
            Assert.Equal(4, images.Length);
            foreach (string imageDirectory in images)
                Assert.Equal(["actual.png", "diff.png", "expected.png"], Directory.GetFiles(imageDirectory).Select(Path.GetFileName).Order());

            using var wholeActual = ReadImage(images, "WholeImage", "actual");
            using var wholeExpected = ReadImage(images, "WholeImage", "expected");
            using var wholeDiff = ReadImage(images, "WholeImage", "diff");
            Assert.Equal((2, 1), (wholeActual.Width, wholeActual.Height));
            Assert.Equal(new SKColor(60, 20, 30, 255), wholeActual.GetPixel(0, 0));
            Assert.Equal(new SKColor(10, 20, 30, 255), wholeExpected.GetPixel(0, 0));
            Assert.Equal(new SKColor(50, 0, 0, 255), wholeDiff.GetPixel(0, 0));
            using var regionDiff = ReadImage(images, "Region", "diff");
            Assert.Equal(new SKColor(0, 0, 0, 255), regionDiff.GetPixel(0, 0));
            Assert.Equal(new SKColor(50, 0, 0, 255), regionDiff.GetPixel(1, 0));
            using var colorExpected = ReadImage(images, "Color", "expected");
            using var colorActual = ReadImage(images, "Color", "actual");
            using var colorDiff = ReadImage(images, "Color", "diff");
            Assert.Equal(new SKColor(4, 5, 6, 255), colorActual.GetPixel(1, 0));
            Assert.Equal(new SKColor(4, 5, 6, 255), colorExpected.GetPixel(0, 0));
            Assert.Equal(new SKColor(100, 110, 120, 255), colorExpected.GetPixel(1, 0));
            Assert.Equal(new SKColor(96, 105, 114, 255), colorDiff.GetPixel(1, 0));
            using var sizeActual = ReadImage(images, "Size", "actual");
            using var sizeExpected = ReadImage(images, "Size", "expected");
            using var sizeDiff = ReadImage(images, "Size", "diff");
            Assert.Equal((2, 1), (sizeActual.Width, sizeActual.Height));
            Assert.Equal((1, 1), (sizeExpected.Width, sizeExpected.Height));
            Assert.Equal((2, 1), (sizeDiff.Width, sizeDiff.Height));
            Assert.Equal(new SKColor(255, 0, 255, 255), sizeDiff.GetPixel(1, 0));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static SKBitmap ReadImage(string[] directories, string test, string image) =>
        SKBitmap.Decode(Path.Combine(Assert.Single(directories, path => Path.GetFileName(path).StartsWith(test + "-", StringComparison.Ordinal)), image + ".png"));
}
