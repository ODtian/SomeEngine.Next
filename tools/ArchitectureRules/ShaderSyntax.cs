namespace SomeEngine.ArchitectureRules;

internal readonly record struct ShaderToken(string Text, int Offset, bool IsIdentifier = false, bool IsString = false);
internal readonly record struct ShaderFunction(string Name, int NameIndex, int BodyStart, int BodyEnd);
internal readonly record struct ShaderControl(string Keyword, int HeaderStart, int HeaderEnd, int BodyStart, int BodyEnd);
internal readonly record struct ShaderAssignment(string Target, string Component, int TargetIndex, int ValueStart, int ValueEnd);
internal readonly record struct ShaderType(string Name, string[] Interfaces, int BodyStart, int BodyEnd);
internal readonly record struct ShaderImport(string Name, int NameIndex);
internal readonly record struct ShaderResource(string Name, int NameIndex);

/// <summary>Reads the shader syntax used by these rules: directives, declarations, calls and control flow.</summary>
internal sealed class ShaderSyntax
{
    public string Source { get; }
    public ShaderToken[] Tokens { get; }
    public List<ShaderFunction> Functions { get; } = [];
    public List<ShaderControl> Controls { get; } = [];
    public List<ShaderAssignment> Assignments { get; } = [];
    public List<ShaderType> Types { get; } = [];
    public List<ShaderImport> Imports { get; } = [];
    public List<ShaderResource> Resources { get; } = [];
    private readonly Dictionary<int, int> _closingTokens = [];

    public ShaderSyntax(string source)
    {
        Source = source;
        Tokens = ReadTokens(source).ToArray();
        var openings = new Stack<int>();
        for (int index = 0; index < Tokens.Length; index++)
        {
            string token = Tokens[index].Text;
            if (token is "(" or "[" or "{")
                openings.Push(index);
            else if (token is ")" or "]" or "}" && openings.TryPop(out int opening))
                _closingTokens[opening] = index;
        }

        for (int index = 0; index + 1 < Tokens.Length; index++)
        {
            if (Tokens[index].Text == "import" && (Tokens[index + 1].IsIdentifier || Tokens[index + 1].IsString))
            {
                int current = index + 1;
                string name = Tokens[current++].Text.Trim('"').Replace('\\', '/');
                while (current + 1 < Tokens.Length && Tokens[current].Text == "." && Tokens[current + 1].IsIdentifier)
                {
                    name += "." + Tokens[current + 1].Text;
                    current += 2;
                }
                if (current < Tokens.Length && Tokens[current].Text == ";") Imports.Add(new(name, index + 1));
            }
            if (IsResourceType(Tokens[index].Text))
            {
                int name = AfterTypeArguments(index + 1);
                if (name >= 0 && name + 1 < Tokens.Length && Tokens[name].IsIdentifier
                    && Tokens[name + 1].Text is ";" or "[" or ":")
                    Resources.Add(new(Tokens[name].Text, name));
            }
            if (Tokens[index].Text is "struct" or "class" && Tokens[index + 1].IsIdentifier)
            {
                int bases = AfterTypeArguments(index + 2);
                int body = bases;
                var interfaces = new List<string>();
                if (bases >= 0 && bases < Tokens.Length && Tokens[bases].Text == ":")
                {
                    body = bases + 1;
                    while (body < Tokens.Length && Tokens[body].Text is not ("{" or ";")) body++;
                    string? name = null;
                    int depth = 0;
                    for (int current = bases + 1; current < body; current++)
                    {
                        ShaderToken token = Tokens[current];
                        if (token.Text == "<") depth++;
                        else if (token.Text == ">") depth--;
                        else if (depth == 0 && token.IsIdentifier) name = token.Text;
                        else if (depth == 0 && token.Text == ",")
                        {
                            if (name is not null) interfaces.Add(name);
                            name = null;
                        }
                    }
                    if (name is not null) interfaces.Add(name);
                }
                if (body >= 0 && body < Tokens.Length && Tokens[body].Text == "{")
                    Types.Add(new(Tokens[index + 1].Text, interfaces.ToArray(), body, End(body)));
            }
            if (Tokens[index].IsIdentifier && Tokens[index + 1].Text == "[")
            {
                int targetEnd = End(index + 1) + 1;
                string component = "";
                if (targetEnd + 1 < Tokens.Length && Tokens[targetEnd].Text == ".")
                {
                    component = Tokens[targetEnd + 1].Text;
                    targetEnd += 2;
                }
                if (targetEnd + 1 < Tokens.Length && Tokens[targetEnd].Text == "=" && Tokens[targetEnd + 1].Text != "=")
                    Assignments.Add(new(Tokens[index].Text, component, index, targetEnd + 1, StatementEnd(targetEnd + 1)));
            }
            if (Tokens[index].Text is "for" or "if" or "while" or "switch" && Tokens[index + 1].Text == "(")
            {
                int end = End(index + 1);
                if (end + 1 < Tokens.Length)
                    Controls.Add(new(Tokens[index].Text, index + 2, end, end + 1, StatementEnd(end + 1)));
            }
            else if (Tokens[index].IsIdentifier && Tokens[index].Text is not ("else" or "return" or "sizeof"))
            {
                int parameters = Parameters(index);
                if (parameters < 0)
                    continue;
                int end = End(parameters) + 1;
                if (end < Tokens.Length && Tokens[end].Text == ":")
                    end += 2;
                if (end < Tokens.Length && Tokens[end].Text == "{")
                    Functions.Add(new(Tokens[index].Text, index, end, End(end)));
            }
        }
    }

    public int End(int index) => _closingTokens.TryGetValue(index, out int end) ? end : index;

    public int Parameters(int nameIndex)
    {
        int index = AfterTypeArguments(nameIndex + 1);
        return index >= 0 && index < Tokens.Length && Tokens[index].Text == "(" ? index : -1;
    }

    private int AfterTypeArguments(int index)
    {
        if (index < Tokens.Length && Tokens[index].Text == "<")
        {
            int depth = 1;
            index++;
            while (index < Tokens.Length && depth != 0)
            {
                if (Tokens[index].Text is ";" or "=" or "+" or "-" or "{" or "}" or "(" or ")") return -1;
                if (Tokens[index].Text == "<") depth++;
                if (Tokens[index].Text == ">") depth--;
                index++;
            }
        }
        return index;
    }

    public IEnumerable<ShaderToken> Between(int first, int end) => Tokens.Skip(first).Take(end - first);

    public bool HasInclude(string name) => Enumerable.Range(0, Math.Max(0, Tokens.Length - 2)).Any(index =>
        Tokens[index].Text == "#" && Tokens[index + 1].Text == "include" && Tokens[index + 2].IsString
        && Tokens[index + 2].Text.Trim('"', '<', '>') == name);

    public bool HasImport(string name) => Imports.Any(import => ImportName(import.Name) == ImportName(name));

    public bool HasCall(string name) => Enumerable.Range(0, Tokens.Length).Any(index =>
        Tokens[index].Text == name && IsCall(index));

    public bool IsCall(int index) => Tokens[index].IsIdentifier && Parameters(index) >= 0
        && Functions.All(function => function.NameIndex != index);

    public string? ContainingType(int index) => Types.Where(type => type.BodyStart < index && index < type.BodyEnd)
        .OrderBy(type => type.BodyEnd - type.BodyStart).Select(type => type.Name).FirstOrDefault();

    public bool HasResource(string name) => Resources.Any(resource => resource.Name == name);

    private static string ImportName(string name) =>
        (name.EndsWith(".slang", StringComparison.Ordinal) ? name[..^6] : name).Replace('/', '.');

    private static bool IsResourceType(string name) => name is
        "ConstantBuffer" or "ParameterBlock" or "StructuredBuffer" or "RWStructuredBuffer" or
        "AppendStructuredBuffer" or "ConsumeStructuredBuffer" or "ByteAddressBuffer" or "RWByteAddressBuffer" or
        "Buffer" or "RWBuffer" or "SamplerState" or "SamplerComparisonState" or "RaytracingAccelerationStructure"
        or "Texture1D" or "Texture1DArray" or "Texture2D" or "Texture2DArray" or "Texture2DMS" or "Texture2DMSArray"
        or "Texture3D" or "TextureCube" or "TextureCubeArray" or "RWTexture1D" or "RWTexture1DArray"
        or "RWTexture2D" or "RWTexture2DArray" or "RWTexture3D" or "RasterizerOrderedBuffer"
        or "RasterizerOrderedByteAddressBuffer" or "RasterizerOrderedStructuredBuffer" or "RasterizerOrderedTexture1D"
        or "RasterizerOrderedTexture1DArray" or "RasterizerOrderedTexture2D" or "RasterizerOrderedTexture2DArray"
        or "RasterizerOrderedTexture3D";

    private int StatementEnd(int index)
    {
        if (Tokens[index].Text == "{")
            return End(index);
        for (int current = index; current < Tokens.Length; current++)
        {
            if (Tokens[current].Text == ";") return current;
            if (Tokens[current].Text is "(" or "[") current = End(current);
        }
        return Tokens.Length - 1;
    }

    private static IEnumerable<ShaderToken> ReadTokens(string source)
    {
        for (int index = 0; index < source.Length;)
        {
            char value = source[index];
            if (char.IsWhiteSpace(value)) { index++; continue; }
            if (value == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                while (index < source.Length && source[index] != '\n') index++;
                continue;
            }
            if (value == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < source.Length && (source[index] != '*' || source[index + 1] != '/')) index++;
                index = Math.Min(index + 2, source.Length);
                continue;
            }
            int start = index++;
            if (char.IsLetter(value) || value == '_')
            {
                while (index < source.Length && (char.IsLetterOrDigit(source[index]) || source[index] == '_')) index++;
                yield return new(source[start..index], start, IsIdentifier: true);
            }
            else if (value is '"' or '\'')
            {
                while (index < source.Length)
                {
                    char character = source[index++];
                    if (character == '\\' && index < source.Length) index++;
                    else if (character == value) break;
                }
                yield return new(source[start..index], start, IsString: true);
            }
            else if (char.IsDigit(value))
            {
                while (index < source.Length && (char.IsLetterOrDigit(source[index]) || source[index] == '.')) index++;
                yield return new(source[start..index], start);
            }
            else
                yield return new(source[start..index], start);
        }
    }
}
