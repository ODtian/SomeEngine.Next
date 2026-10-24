namespace SomeEngine.Graphics.Direct3D12.Tests;

[Xunit.CollectionDefinition("Direct3D12 shader compilation", DisableParallelization = true)]
public sealed class ShaderCompilationCollection : Xunit.ICollectionFixture<D3D12TestCompiler>;
