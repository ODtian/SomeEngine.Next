using Xunit;
using Xunit.Sdk;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[XunitTestCaseDiscoverer("SomeEngine.Testing.HardwareRowsDiscoverer", "SomeEngine.Graphics.Direct3D12.Tests")]
public sealed class HardwareRowsTheoryAttribute : TheoryAttribute;
