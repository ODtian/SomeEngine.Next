using System.Collections.Generic;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace SomeEngine.Testing;

public sealed class HardwareRowsDiscoverer(IMessageSink diagnosticMessages) : TheoryDiscoverer(diagnosticMessages)
{
    protected override IEnumerable<IXunitTestCase> CreateTestCasesForDataRow(
        ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod,
        IAttributeInfo theoryAttribute, object[] dataRow)
    {
        foreach (IXunitTestCase testCase in base.CreateTestCasesForDataRow(discoveryOptions, testMethod, theoryAttribute, dataRow))
        {
            if (dataRow[0] is true)
            {
                if (!testCase.Traits.TryGetValue("Category", out List<string>? categories))
                    testCase.Traits.Add("Category", categories = new List<string>());
                categories.Add("Hardware");
            }
            yield return testCase;
        }
    }
}
