namespace SomeEngine.Graphics.Vulkan.Tests;

[Xunit.CollectionDefinition("Vulkan shader compilation", DisableParallelization = true)]
public sealed class ShaderCompilationCollection : Xunit.ICollectionFixture<VulkanTestCompiler>;
