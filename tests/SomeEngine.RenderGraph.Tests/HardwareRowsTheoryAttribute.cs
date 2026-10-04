using Xunit;
using Xunit.Sdk;

namespace SomeEngine.RenderGraph.Tests;

[XunitTestCaseDiscoverer("SomeEngine.Testing.HardwareRowsDiscoverer", "SomeEngine.RenderGraph.Tests")]
public sealed class HardwareRowsTheoryAttribute : TheoryAttribute;
