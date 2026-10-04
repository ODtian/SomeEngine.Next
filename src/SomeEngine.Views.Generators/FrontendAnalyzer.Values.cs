using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace SomeEngine.Views.Generators;

public sealed partial class FrontendAnalyzer
{
    [Flags]
    private enum ValueSource { None = 0, ReactiveValue = 1, MemoGetter = 2 }

    private sealed class LocalValues
    {
        private readonly Dictionary<ILocalSymbol, List<IOperation>> _values = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<TextSpan, ValueSource> _sources = [];

        internal LocalValues(SyntaxNode function, SyntaxNode[] nodes, SemanticModel model, CancellationToken cancellation)
        {
            foreach (SyntaxNode node in nodes)
            {
                if (node is VariableDeclaratorSyntax && model.GetOperation(node, cancellation) is
                    IVariableDeclaratorOperation { Initializer.Value: { } value } declaration)
                    Add(declaration.Symbol, value);
                else if (node is AssignmentExpressionSyntax && model.GetOperation(node, cancellation) is
                    ISimpleAssignmentOperation { Target: ILocalReferenceOperation local } assignment)
                    Add(local.Local, assignment.Value);
            }
            if (GetFunctionGraph(function, model, cancellation) is { } graph)
                ReadValues(graph, cancellation);
        }

        internal IOperation? GetValue(ILocalReferenceOperation read) => _values.TryGetValue(read.Local, out var values)
            ? values.LastOrDefault(value => value.Syntax.Span.End <= read.Syntax.SpanStart) : null;

        internal bool HasReactiveRead(IOperation value)
        {
            if (value is IAnonymousFunctionOperation or ILocalFunctionOperation or INameOfOperation || IsSharedReference(value.Type))
                return false;
            if (_sources.TryGetValue(value.Syntax.Span, out ValueSource source))
                return (source & ValueSource.ReactiveValue) != 0;
            if (value is IPropertyReferenceOperation property && IsReactiveProperty(property.Property))
                return true;
            return value.ChildOperations.Any(HasReactiveRead);
        }

        private void Add(ILocalSymbol local, IOperation value)
        {
            if (!_values.TryGetValue(local, out var values))
                _values.Add(local, values = []);
            values.Add(value);
        }

        private void ReadValues(ControlFlowGraph graph, CancellationToken cancellation)
        {
            var outputs = graph.Blocks.Select(_ => new ValueReads()).ToArray();
            bool changed;
            do
            {
                changed = false;
                foreach (BasicBlock block in graph.Blocks.Where(block => block.IsReachable))
                {
                    cancellation.ThrowIfCancellationRequested();
                    ValueReads reads = MergeInputs(block, outputs);
                    foreach (IOperation operation in BlockOperations(block))
                        Evaluate(operation, reads, record: false);
                    if (!reads.HasSameValues(outputs[block.Ordinal]))
                    {
                        outputs[block.Ordinal] = reads;
                        changed = true;
                    }
                }
            } while (changed);

            foreach (BasicBlock block in graph.Blocks.Where(block => block.IsReachable))
            {
                ValueReads reads = MergeInputs(block, outputs);
                foreach (IOperation operation in BlockOperations(block))
                    Evaluate(operation, reads, record: true);
            }
        }

        private static ValueReads MergeInputs(BasicBlock block, ValueReads[] outputs)
        {
            var reads = new ValueReads();
            foreach (ControlFlowBranch predecessor in block.Predecessors.Where(branch => branch.Source.IsReachable))
                reads.Merge(outputs[predecessor.Source.Ordinal]);
            return reads;
        }

        private ValueSource Evaluate(IOperation value, ValueReads reads, bool record)
        {
            ValueSource source;
            switch (value)
            {
                case IAnonymousFunctionOperation or IFlowAnonymousFunctionOperation or ILocalFunctionOperation or INameOfOperation:
                    source = ValueSource.None;
                    break;
                case ILocalReferenceOperation local:
                    source = reads.Variables.TryGetValue(local.Local, out var localSource) ? localSource : ValueSource.None;
                    break;
                case IParameterReferenceOperation parameter:
                    source = reads.Variables.TryGetValue(parameter.Parameter, out var parameterSource) ? parameterSource : ValueSource.None;
                    break;
                case IFlowCaptureReferenceOperation capture:
                    source = reads.Captures.TryGetValue(capture.Id, out var capturedSource) ? capturedSource : ValueSource.None;
                    break;
                case IFlowCaptureOperation capture:
                    source = Evaluate(capture.Value, reads, record);
                    reads.Captures[capture.Id] = source;
                    break;
                case ISimpleAssignmentOperation assignment:
                    foreach (IOperation child in assignment.Target.ChildOperations)
                        Evaluate(child, reads, record);
                    source = Evaluate(assignment.Value, reads, record);
                    if (GetVariable(assignment.Target) is { } variable)
                        reads.Variables[variable] = source;
                    break;
                case ICompoundAssignmentOperation assignment:
                    source = Evaluate(assignment.Target, reads, record) | Evaluate(assignment.Value, reads, record);
                    if (GetVariable(assignment.Target) is { } changedVariable)
                        reads.Variables[changedVariable] = source;
                    break;
                case IIncrementOrDecrementOperation change:
                    source = Evaluate(change.Target, reads, record);
                    if (GetVariable(change.Target) is { } changedValue)
                        reads.Variables[changedValue] = source;
                    break;
                case IVariableDeclaratorOperation { Initializer.Value: { } initial } declaration:
                    source = Evaluate(initial, reads, record);
                    reads.Variables[declaration.Symbol] = source;
                    break;
                default:
                    source = ValueSource.None;
                    foreach (IOperation child in value.ChildOperations)
                        source |= Evaluate(child, reads, record);
                    if (value is IPropertyReferenceOperation property && IsReactiveProperty(property.Property))
                        source |= ValueSource.ReactiveValue;
                    if (value is IInvocationOperation call)
                    {
                        if (call.TargetMethod.Name == "Memo" && call.TargetMethod.ContainingType.ToDisplayString() == "SomeEngine.Views.Solid.Solid")
                            source = ValueSource.MemoGetter;
                        else if (call.TargetMethod.MethodKind == MethodKind.DelegateInvoke && call.Instance is { } getter &&
                            (Evaluate(getter, reads, record) & ValueSource.MemoGetter) != 0)
                            source |= ValueSource.ReactiveValue;
                    }
                    if (value.Type?.TypeKind != TypeKind.Delegate)
                        source &= ValueSource.ReactiveValue;
                    break;
            }
            if (IsSharedReference(value.Type) && value.Type?.TypeKind != TypeKind.Delegate)
                source = ValueSource.None;
            if (record)
                _sources[value.Syntax.Span] = _sources.TryGetValue(value.Syntax.Span, out var previous) ? previous | source : source;
            return source;
        }

        private static ISymbol? GetVariable(IOperation value) => value switch
        {
            ILocalReferenceOperation local => local.Local,
            IParameterReferenceOperation parameter => parameter.Parameter,
            _ => null,
        };
    }

    private sealed class ValueReads
    {
        internal readonly Dictionary<ISymbol, ValueSource> Variables = new(SymbolEqualityComparer.Default);
        internal readonly Dictionary<CaptureId, ValueSource> Captures = [];

        internal void Merge(ValueReads other)
        {
            foreach (var pair in other.Variables)
                Variables[pair.Key] = Variables.TryGetValue(pair.Key, out var source) ? source | pair.Value : pair.Value;
            foreach (var pair in other.Captures)
                Captures[pair.Key] = Captures.TryGetValue(pair.Key, out var source) ? source | pair.Value : pair.Value;
        }

        internal bool HasSameValues(ValueReads other) => Variables.Count == other.Variables.Count && Captures.Count == other.Captures.Count &&
            Variables.All(pair => other.Variables.TryGetValue(pair.Key, out var source) && source == pair.Value) &&
            Captures.All(pair => other.Captures.TryGetValue(pair.Key, out var source) && source == pair.Value);
    }

    private static IEnumerable<IOperation> BlockOperations(BasicBlock block)
    {
        foreach (IOperation operation in block.Operations)
            yield return operation;
        if (block.BranchValue is { } branch)
            yield return branch;
    }

    private static ControlFlowGraph? GetFunctionGraph(SyntaxNode function, SemanticModel model, CancellationToken cancellation)
    {
        IOperation? root = model.GetOperation(function, cancellation);
        while (root?.Parent is { } parent)
            root = parent;
        ControlFlowGraph? graph = root switch
        {
            IMethodBodyOperation body => ControlFlowGraph.Create(body, cancellation),
            IConstructorBodyOperation body => ControlFlowGraph.Create(body, cancellation),
            IBlockOperation body => ControlFlowGraph.Create(body, cancellation),
            IFieldInitializerOperation initial => ControlFlowGraph.Create(initial, cancellation),
            IPropertyInitializerOperation initial => ControlFlowGraph.Create(initial, cancellation),
            _ => null,
        };
        foreach (SyntaxNode nested in function.AncestorsAndSelf().Reverse().Where(node =>
            node is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
        {
            if (graph is null)
                break;
            if (nested is LocalFunctionStatementSyntax local && model.GetDeclaredSymbol(local, cancellation) is IMethodSymbol method)
                graph = graph.GetLocalFunctionControlFlowGraph(method, cancellation);
            else if (model.GetSymbolInfo(nested, cancellation).Symbol is IMethodSymbol methodSymbol)
            {
                IFlowAnonymousFunctionOperation? lambda = graph.Blocks.SelectMany(BlockOperations).SelectMany(OperationTree)
                    .OfType<IFlowAnonymousFunctionOperation>().FirstOrDefault(value =>
                        SymbolEqualityComparer.Default.Equals(value.Symbol, methodSymbol));
                graph = lambda is null ? null : graph.GetAnonymousFunctionControlFlowGraph(lambda, cancellation);
            }
        }
        return graph;
    }

    private static IEnumerable<IOperation> OperationTree(IOperation operation)
    {
        yield return operation;
        foreach (IOperation child in operation.ChildOperations)
            foreach (IOperation descendant in OperationTree(child))
                yield return descendant;
    }
}
