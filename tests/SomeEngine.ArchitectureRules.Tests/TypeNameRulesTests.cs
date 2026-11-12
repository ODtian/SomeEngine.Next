using SomeEngine.ArchitectureRules;

namespace SomeEngine.ArchitectureRules.Tests;

public sealed class TypeNameRulesTests
{
    [Fact]
    public void NewTypeReportsItsForbiddenWordAndSourcePosition()
    {
        var violations = TypeNameRules.CheckSource("Changed.cs", "namespace Example; public class RuntimeCache;");
        TypeNameViolation violation = Assert.Single(violations);
        Assert.Equal("RuntimeCache", violation.Name);
        Assert.Equal("Runtime", violation.Word);
        Assert.Equal("Changed.cs", violation.Path);
        Assert.Equal(1, violation.Line);
        Assert.True(violation.Column > 1);
    }

    [Fact]
    public void ExistingTypeKeepsItsNameWhenItsFileMoves()
    {
        const string source = "namespace Example; public class RuntimeCache;";
        Assert.Empty(TypeNameRules.CheckSource("Moved.cs", source, source));
    }

    [Fact]
    public void ConcreteTypeNamesIgnoreWordsInsideCommentsAndStrings()
    {
        const string source = """
            namespace Example;
            // class RuntimeCache;
            public class ContactPairs { public const string Sample = "class Snapshot;"; }
            """;
        Assert.Empty(TypeNameRules.CheckSource("ContactPairs.cs", source));
    }
}
