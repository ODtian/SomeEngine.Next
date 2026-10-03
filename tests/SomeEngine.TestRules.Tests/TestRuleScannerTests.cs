using SomeEngine.TestRules;

namespace SomeEngine.TestRules.Tests;

public class TestRuleScannerTests
{
    [Theory]
    [InlineData("Assert.Contains(\"fault\", exception.Message);")]
    [InlineData("Assert.Equal(\"fault\", exception.Message);")]
    [InlineData("Assert.Matches(\"fault.*\", exception.Message);")]
    [InlineData("Assert.True(exception.Message.Contains(\"fault\"));")]
    [InlineData("Assert.Contains(exceptions, exception => exception.Message == \"fault\");")]
    [InlineData("Assert.Equal(\"fault\", Assert.Throws<InvalidOperationException>(Run).Message);")]
    [InlineData("var text = exception.Message; var matches = text.Contains(\"fault\"); Assert.True(matches);")]
    [InlineData("string text; text = exception.Message; Assert.Equal(\"fault\", text);")]
    [InlineData("Assert.Equal(\"fault\", exception?.Message);")]
    public void ExceptionMessageAssertionsReportTheirRule(string statement)
    {
        var source = "class Example { void RejectsInput() { " + statement + " } }";

        var violation = Assert.Single(TestRuleScanner.ReadCSharp("Tests.cs", source, true, "Example.Tests"));

        Assert.Equal("TEST002", violation.Rule);
    }

    [Theory]
    [InlineData("Assert.Throws<ArgumentException>(Run); Assert.Equal(\"amount\", error.ParamName);")]
    [InlineData("Assert.Equal(FailureCode.InvalidInput, result.ErrorCode);")]
    [InlineData("Assert.True(completed, exception.Message);")]
    [InlineData("Assert.Fail(exception.Message);")]
    [InlineData("// Thread.Sleep(1); Assert.Equal(\"fault\", error.Message);\nvar text = \"Thread.Sleep(1)\"; Assert.Equal(\"Thread.Sleep(1)\", text);")]
    [InlineData("var source = \"\"\"Assert.Equal(\"fault\", error.Message); Thread.Sleep(1);\"\"\"; Assert.NotNull(source);")]
    public void StructuredFailuresAndDiagnosticTextPass(string statement)
    {
        var source = "class Example { void ChecksBehavior() { " + statement + " } }";

        Assert.Empty(TestRuleScanner.ReadCSharp("Tests.cs", source, true, "Example.Tests"));
    }

    [Fact]
    public void OrdinaryMessageDataPasses()
    {
        var source = """
            class Result { public string Message { get; set; } }
            class Example { void ReturnsMessage() { var result = new Result(); Assert.Equal("hello", result.Message); } }
            """;

        Assert.Empty(TestRuleScanner.ReadCSharp("Tests.cs", source, true, "Example.Tests"));
    }

    [Theory]
    [InlineData("", "Thread.Sleep(1)")]
    [InlineData("", "global::System.Threading.Thread.Sleep(1)")]
    [InlineData("using Sleeper = System.Threading.Thread;", "Sleeper.Sleep(1)")]
    [InlineData("using Threading = System.Threading;", "Threading.Thread.Sleep(1)")]
    [InlineData("using static System.Threading.Thread;", "Sleep(1)")]
    public void ThreadSleepReportsItsRule(string imports, string expression)
    {
        var source = imports + " class Example { void Waits() { " + expression + "; } }";

        var violation = Assert.Single(TestRuleScanner.ReadCSharp("Tests.cs", source, true, "Example.Tests"));

        Assert.Equal("TEST003", violation.Rule);
    }

    [Theory]
    [InlineData("using Xunit;", "Assert.Equal(\"fault\", error.Message);")]
    [InlineData("using Check = Xunit.Assert;", "Check.Contains(\"fault\", error.Message);")]
    [InlineData("using static Xunit.Assert;", "Equal(\"fault\", error.Message);")]
    [InlineData("using static global::Xunit.Assert;", "Equal(\"fault\", error.Message);")]
    public void AssertionImportsReportExceptionMessages(string imports, string statement)
    {
        var source = imports + " class Example { void Rejects() { " + statement + " } }";

        Assert.Equal("TEST002", Assert.Single(TestRuleScanner.ReadCSharp("Tests.cs", source, true, "Example.Tests")).Rule);
    }

    [Theory]
    [InlineData("using System.Runtime.CompilerServices;", "InternalsVisibleTo(\"Example.Tests\")")]
    [InlineData("", "System.Runtime.CompilerServices.InternalsVisibleToAttribute(\"Example.Tests, PublicKey=1234\")")]
    [InlineData("using Friend = System.Runtime.CompilerServices.InternalsVisibleToAttribute;", "Friend(\"Example.Tests\")")]
    [InlineData("using Compiler = System.Runtime.CompilerServices;", "Compiler.InternalsVisibleTo(\"Example.Tests\")")]
    [InlineData("", "System.Runtime.CompilerServices.InternalsVisibleTo(\"Example\" + \".Tests\")")]
    public void TestAssemblyAccessReportsItsRule(string imports, string attribute)
    {
        var source = imports + " [assembly: " + attribute + "]";

        var violation = Assert.Single(TestRuleScanner.ReadCSharp("Library.cs", source, false, "Example.Tests"));

        Assert.Equal("TEST001", violation.Rule);
    }

    [Fact]
    public void ProductionAssemblyAccessPasses()
    {
        var source = "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"Example.Render\")]";

        Assert.Empty(TestRuleScanner.ReadCSharp("Library.cs", source, false, "Example.Tests"));
    }

    [Theory]
    [InlineData("<InternalsVisibleTo Include=\"Example.Tests\" />")]
    [InlineData("<InternalsVisibleTo Update=\"Example.Tests\" />")]
    [InlineData("<AssemblyAttribute Include=\"System.Runtime.CompilerServices.InternalsVisibleToAttribute\"><_Parameter1>Example.Tests</_Parameter1></AssemblyAttribute>")]
    [InlineData("<InternalsVisibleTo Include=\"Example.Render;Example.Tests\" />")]
    [InlineData("<AssemblyAttribute Include=\"System.Runtime.CompilerServices.InternalsVisibleTo\"><_Parameter1>Example.Tests</_Parameter1></AssemblyAttribute>")]
    public void BuildDeclarationsReportTestAssemblyAccess(string item)
    {
        var source = "<Project>\n<ItemGroup>\n" + item + "\n</ItemGroup>\n</Project>";

        var violation = Assert.Single(TestRuleScanner.ReadBuildFile("Library.csproj", source, "Example.Tests"));

        Assert.Equal("TEST001", violation.Rule);
        Assert.Equal(3, violation.Line);
    }

    [Fact]
    public void ConditionalBranchesKeepTheirSourcePositionAndImports()
    {
        var source = """
            using Sleeper = System.Threading.Thread;
            class Example
            {
                void Waits()
                {
            #if SPECIAL_BUILD
                    Sleeper.Sleep(1);
            #else
                    Thread.Sleep(2);
            #endif
                }
            }
            """;

        var violations = TestRuleScanner.ReadCSharp("Tests.cs", source, true, "Example.Tests");

        Assert.Collection(violations,
            first => { Assert.Equal(7, first.Line); Assert.Equal("Example.Waits", first.Member); },
            second => Assert.Equal(9, second.Line));
    }

    [Fact]
    public void ANewDuplicateExceedsItsWhitelistEntry()
    {
        var first = "class Example { void Waits() { Thread.Sleep(1); } }";
        var existing = Assert.Single(TestRuleScanner.ReadCSharp("Tests.cs", first, true, "Example.Tests"));
        var whitelist = new[] { "[Example.Tests]", existing.WhitelistEntry };
        var changed = "class Example { void Waits() { Thread.Sleep(1); Thread.Sleep(1); } }";

        var violations = TestRuleScanner.ApplyWhitelist(TestRuleScanner.ReadCSharp("Tests.cs", changed, true, "Example.Tests"), "Example.Tests", whitelist);

        Assert.Single(violations);
    }

    [Fact]
    public void WhitelistedViolationsSurviveLineAndSpacingChanges()
    {
        var original = Assert.Single(TestRuleScanner.ReadCSharp("Tests.cs", "class Example { void Waits() { Thread.Sleep(1); } }", true, "Example.Tests"));
        var whitelist = new[] { "[Example.Tests]", original.WhitelistEntry };
        var changed = "\n\nclass Example { void Waits() { Thread . Sleep ( 1 ); } }";

        Assert.Empty(TestRuleScanner.ApplyWhitelist(TestRuleScanner.ReadCSharp("Tests.cs", changed, true, "Example.Tests"), "Example.Tests", whitelist));
    }

    [Fact]
    public void WhitelistEntriesBelongToTheirTestProject()
    {
        var violation = Assert.Single(TestRuleScanner.ReadCSharp("Tests.cs", "Thread.Sleep(1);", true, "Example.Tests"));
        var whitelist = new[] { "[Other.Tests]", violation.WhitelistEntry };

        Assert.Single(TestRuleScanner.ApplyWhitelist(new[] { violation }, "Example.Tests", whitelist));
    }

    [Fact]
    public void MovingAViolationToANewBehaviorReportsIt()
    {
        var original = Assert.Single(TestRuleScanner.ReadCSharp("Tests.cs", "class Example { void First() { Thread.Sleep(1); } }", true, "Example.Tests"));
        var whitelist = new[] { "[Example.Tests]", original.WhitelistEntry };
        var changed = "class Example { void Second() { Thread.Sleep(1); } }";

        Assert.Single(TestRuleScanner.ApplyWhitelist(TestRuleScanner.ReadCSharp("Tests.cs", changed, true, "Example.Tests"), "Example.Tests", whitelist));
    }

    [Fact]
    public void ProductionSleepCallsPass()
    {
        Assert.Empty(TestRuleScanner.ReadCSharp("Library.cs", "Thread.Sleep(1);", false, "Example.Tests"));
    }
}
