using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Abraxius.Protocol;

namespace Abraxius.Axl;

/// <summary>Strict, allocation-conscious parser for the deliberately small AXL text language.</summary>
public static class AxlParser
{
    private static readonly IAxlSchemaRegistry DefaultSchemas = AxlSchemaRegistry.CreateDefault();

    public static AxlParseResult Parse(string text, AxlLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        limits ??= new AxlLimits();
        if (Encoding.UTF8.GetByteCount(text) > limits.MaxDocumentBytes)
        {
            return Failure(AxlParseStatus.Invalid, new AxlDiagnostic(AxlDiagnosticSeverity.Error, AxlDiagnosticCode.LimitExceeded, "AXL document exceeds the configured byte limit."));
        }

        var lexer = new Lexer(text, limits);
        var tokens = lexer.Tokenize();
        if (lexer.Diagnostics.Count > 0)
        {
            return Failure(AxlParseStatus.Invalid, lexer.Diagnostics.ToImmutableArray());
        }

        var parser = new Parser(text, tokens, limits);
        return parser.ParseDocument();
    }

    public static AxlParseResult Parse(ReadOnlySpan<byte> utf8, AxlLimits? limits = null)
    {
        try
        {
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(utf8);
            return Parse(text, limits);
        }
        catch (DecoderFallbackException)
        {
            return Failure(AxlParseStatus.Invalid, new AxlDiagnostic(AxlDiagnosticSeverity.Error, AxlDiagnosticCode.InvalidUtf8, "AXL input is not valid UTF-8."));
        }
    }

    private static AxlParseResult Failure(AxlParseStatus status, params AxlDiagnostic[] diagnostics) =>
        new(status, null, diagnostics.ToImmutableArray());

    private static AxlParseResult Failure(AxlParseStatus status, ImmutableArray<AxlDiagnostic> diagnostics) =>
        new(status, null, diagnostics);

    private enum TokenKind
    {
        Identifier,
        String,
        Number,
        Equals,
        OpenBrace,
        CloseBrace,
        OpenBracket,
        CloseBracket,
        Comma,
        End
    }

    private readonly record struct Token(TokenKind Kind, string Text, AxlSourceSpan Span);

    private sealed class Lexer
    {
        private readonly string _text;
        private readonly AxlLimits _limits;
        private readonly List<AxlDiagnostic> _diagnostics = [];
        private readonly List<Token> _tokens = [];
        private int _offset;
        private int _line = 1;
        private int _column = 1;

        public Lexer(string text, AxlLimits limits)
        {
            _text = text;
            _limits = limits;
        }

        public IReadOnlyList<AxlDiagnostic> Diagnostics => _diagnostics;

        public IReadOnlyList<Token> Tokenize()
        {
            while (_offset < _text.Length)
            {
                if (char.IsWhiteSpace(_text[_offset]))
                {
                    Advance(_text[_offset]);
                    continue;
                }

                var startOffset = _offset;
                var startLine = _line;
                var startColumn = _column;
                var character = _text[_offset];
                switch (character)
                {
                    case '=':
                        Add(TokenKind.Equals, "=", startOffset, startLine, startColumn);
                        Advance(character);
                        break;
                    case '{':
                        Add(TokenKind.OpenBrace, "{", startOffset, startLine, startColumn);
                        Advance(character);
                        break;
                    case '}':
                        Add(TokenKind.CloseBrace, "}", startOffset, startLine, startColumn);
                        Advance(character);
                        break;
                    case '[':
                        Add(TokenKind.OpenBracket, "[", startOffset, startLine, startColumn);
                        Advance(character);
                        break;
                    case ']':
                        Add(TokenKind.CloseBracket, "]", startOffset, startLine, startColumn);
                        Advance(character);
                        break;
                    case ',':
                        Add(TokenKind.Comma, ",", startOffset, startLine, startColumn);
                        Advance(character);
                        break;
                    case '"':
                        ReadString(startOffset, startLine, startColumn);
                        break;
                    default:
                        if (IsIdentifierCharacter(character))
                        {
                            ReadIdentifier(startOffset, startLine, startColumn);
                        }
                        else
                        {
                            AddDiagnostic(AxlDiagnosticCode.InvalidIdentifier, $"Unexpected character '{character}'.", startOffset, 1, startLine, startColumn);
                            Advance(character);
                        }

                        break;
                }
            }

            _tokens.Add(new(TokenKind.End, string.Empty, new AxlSourceSpan(_offset, 0, _line, _column)));
            return _tokens;
        }

        private void ReadIdentifier(int startOffset, int startLine, int startColumn)
        {
            while (_offset < _text.Length && IsIdentifierCharacter(_text[_offset]))
            {
                Advance(_text[_offset]);
            }

            var value = _text[startOffset.._offset];
            var kind = value.Length > 0 && (char.IsDigit(value[0]) || value[0] == '-')
                ? TokenKind.Number
                : TokenKind.Identifier;
            Add(kind, value, startOffset, startLine, startColumn);
        }

        private void ReadString(int startOffset, int startLine, int startColumn)
        {
            Advance('"');
            var triple = _offset + 1 < _text.Length && _text[_offset] == '"' && _text[_offset + 1] == '"';
            if (triple)
            {
                Advance('"');
                Advance('"');
            }

            var builder = new StringBuilder();
            while (_offset < _text.Length)
            {
                if (triple && _offset + 2 < _text.Length && _text[_offset] == '"' && _text[_offset + 1] == '"' && _text[_offset + 2] == '"')
                {
                    Advance('"');
                    Advance('"');
                    Advance('"');
                    Add(TokenKind.String, builder.ToString(), startOffset, startLine, startColumn);
                    return;
                }

                if (!triple && _text[_offset] == '"')
                {
                    Advance('"');
                    Add(TokenKind.String, builder.ToString(), startOffset, startLine, startColumn);
                    return;
                }

                if (!triple && _text[_offset] == '\\')
                {
                    Advance('\\');
                    if (_offset >= _text.Length)
                    {
                        break;
                    }

                    var escaped = _text[_offset];
                    Advance(escaped);
                    switch (escaped)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (!TryReadUnicodeEscape(builder))
                            {
                                AddDiagnostic(AxlDiagnosticCode.InvalidString, "Invalid Unicode escape sequence.", startOffset, Math.Max(1, _offset - startOffset), startLine, startColumn);
                                return;
                            }

                            break;
                        default:
                            AddDiagnostic(AxlDiagnosticCode.InvalidString, $"Unsupported escape '\\{escaped}'.", startOffset, Math.Max(1, _offset - startOffset), startLine, startColumn);
                            return;
                    }
                }
                else
                {
                    builder.Append(_text[_offset]);
                    Advance(_text[_offset]);
                }

                if (builder.Length > _limits.MaxStringLength)
                {
                    AddDiagnostic(AxlDiagnosticCode.LimitExceeded, "AXL string exceeds the configured length limit.", startOffset, _offset - startOffset, startLine, startColumn);
                    return;
                }
            }

            AddDiagnostic(AxlDiagnosticCode.UnexpectedEnd, "Unterminated AXL string.", startOffset, Math.Max(1, _offset - startOffset), startLine, startColumn);
        }

        private bool TryReadUnicodeEscape(StringBuilder builder)
        {
            if (_offset + 4 > _text.Length)
            {
                return false;
            }

            var digits = _text.AsSpan(_offset, 4);
            if (!ushort.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint))
            {
                return false;
            }

            builder.Append((char)codePoint);
            for (var i = 0; i < 4; i++)
            {
                Advance(_text[_offset]);
            }

            return true;
        }

        private void Add(TokenKind kind, string value, int startOffset, int startLine, int startColumn) =>
            _tokens.Add(new(kind, value, new AxlSourceSpan(startOffset, Math.Max(1, _offset - startOffset + (kind is TokenKind.Equals or TokenKind.OpenBrace or TokenKind.CloseBrace or TokenKind.OpenBracket or TokenKind.CloseBracket or TokenKind.Comma ? 1 : 0)), startLine, startColumn)));

        private void AddDiagnostic(AxlDiagnosticCode code, string message, int offset, int length, int line, int column) =>
            _diagnostics.Add(new(AxlDiagnosticSeverity.Error, code, message, new AxlSourceSpan(offset, length, line, column)));

        private void Advance(char character)
        {
            _offset++;
            if (character == '\n')
            {
                _line++;
                _column = 1;
            }
            else
            {
                _column++;
            }
        }

        private static bool IsIdentifierCharacter(char character) =>
            char.IsLetterOrDigit(character) || character is '_' or '-' or '.' or '/' or ':' or '@' or '#';
    }

    private sealed class Parser
    {
        private readonly string _source;
        private readonly IReadOnlyList<Token> _tokens;
        private readonly AxlLimits _limits;
        private readonly List<AxlDiagnostic> _diagnostics = [];
        private int _position;

        public Parser(string source, IReadOnlyList<Token> tokens, AxlLimits limits)
        {
            _source = source;
            _tokens = tokens;
            _limits = limits;
        }

        public AxlParseResult ParseDocument()
        {
            if (!TryConsume(TokenKind.Identifier, out var header) || !AxlVersion.TryParse(header.Text, out var version))
            {
                Add(AxlDiagnosticCode.InvalidHeader, "AXL document must begin with axl/<major>[.<minor]>.", Current.Span);
                return Finish(AxlParseStatus.Invalid);
            }

            if (version.Major != AxlVersion.Current.Major || version.Minor > AxlVersion.Current.Minor)
            {
                Add(AxlDiagnosticCode.UnsupportedVersion, $"AXL version {version} is not supported by this runtime.", header.Span);
                return Finish(AxlParseStatus.UnsupportedVersion);
            }

            var commands = ImmutableArray.CreateBuilder<AxlCommand>();
            if (TryConsumeIdentifier("batch"))
            {
                if (!Expect(TokenKind.OpenBrace, "'{'") )
                {
                    return Finish(AxlParseStatus.Invalid);
                }

                while (Current.Kind != TokenKind.CloseBrace && Current.Kind != TokenKind.End)
                {
                    if (commands.Count >= _limits.MaxCommands)
                    {
                        Add(AxlDiagnosticCode.LimitExceeded, "AXL command count exceeds the configured limit.", Current.Span);
                        return Finish(AxlParseStatus.Invalid);
                    }

                    var command = ParseCommand();
                    if (command is not null)
                    {
                        commands.Add(command);
                    }
                    else if (Current.Kind != TokenKind.End && Current.Kind != TokenKind.CloseBrace)
                    {
                        RecoverToCommandBoundary();
                    }
                }

                if (!Expect(TokenKind.CloseBrace, "'}'"))
                {
                    return Finish(AxlParseStatus.Incomplete);
                }
            }
            else
            {
                var command = ParseCommand();
                if (command is not null)
                {
                    commands.Add(command);
                }
            }

            if (Current.Kind != TokenKind.End)
            {
                Add(AxlDiagnosticCode.UnexpectedToken, "Only one command or one batch is allowed per AXL document.", Current.Span);
            }

            if (_diagnostics.Any(static diagnostic => diagnostic.Severity == AxlDiagnosticSeverity.Error))
            {
                return Finish(_diagnostics.Any(static diagnostic => diagnostic.Code == AxlDiagnosticCode.UnexpectedEnd) ? AxlParseStatus.Incomplete : AxlParseStatus.Invalid);
            }

            return AxlParseResult.Success(new AxlDocument(version, commands.ToImmutable()));
        }

        private AxlCommand? ParseCommand()
        {
            AxlCommandId? id = null;
            if (Current.Kind == TokenKind.Identifier && AxlCommandId.TryParse(Current.Text, out var parsedId))
            {
                id = parsedId;
                Move();
            }

            if (Current.Kind != TokenKind.Identifier)
            {
                Add(AxlDiagnosticCode.UnexpectedToken, "A command name is required.", Current.Span, "command", Current.Text);
                return null;
            }

            var name = Current.Text;
            Move();
            if (name.Equals("find", StringComparison.Ordinal))
            {
                if (!TryConsumeIdentifier("code"))
                {
                    Add(AxlDiagnosticCode.UnknownCommand, "Only find code is currently supported.", Previous.Span);
                    return null;
                }

                var fields = ParseFields("find.code");
                if (!TryText(fields, "q", out var query))
                {
                    return null;
                }

                var limit = TryInteger(fields, "lim", 20, min: 1, max: 10_000);
                var scope = TryReference(fields, "scope");
                return new AxlFindCode(query, limit, scope, id);
            }

            return name switch
            {
                "call" => ParseCall(id),
                "memory" => ParseMemory(id),
                "synth" => ParseSynthesis(id),
                "verify" => ParseVerify(id),
                "intent" => ParseIntent(id),
                "delegate" => ParseDelegate(id),
                "ret" => ParseResult(id),
                "state" => ParseState(id),
                "skill" => ParseSkill(id),
                _ => UnknownCommand(name)
            };
        }

        private AxlCommand? ParseCall(AxlCommandId? id)
        {
            AxlReference? capability = null;
            if (Current.Kind is TokenKind.Identifier or TokenKind.String && AxlReference.TryParse(Current.Text, out var positionalCapability))
            {
                capability = positionalCapability;
                Move();
            }

            var fields = ParseFields("call");
            capability ??= TryReference(fields, "cap");
            if (capability is null || capability.Value.Kind != AxlReferenceKind.Capability)
            {
                Add(AxlDiagnosticCode.InvalidReference, "call requires a capability reference such as @cap:git.status.", Current.Span);
                return null;
            }

            var operation = TryText(fields, "op", out var parsedOperation)
                ? parsedOperation
                : capability.Value.Value[(capability.Value.Value.LastIndexOf('.') + 1)..];
            var target = TryText(fields, "target", out var parsedTarget) ? parsedTarget : "current_project";
            var mutation = TryBoolean(fields, "mutation", false);
            var parameters = fields.ToParameters(new HashSet<string>(["cap", "op", "target", "mutation", "args"], StringComparer.Ordinal));
            if (fields.TryGetValue("args", out var args) && args is AxlValue.Record record)
            {
                foreach (var pair in record.Fields)
                {
                    parameters = parameters.SetItem(pair.Key, pair.Value);
                }
            }

            return new AxlCapabilityCall(capability.Value, operation, target, parameters, mutation, id);
        }

        private AxlCommand? ParseMemory(AxlCommandId? id)
        {
            if (TryConsumeIdentifier("query"))
            {
                // Long form: memory query q="..."
            }

            var fields = ParseFields("memory.query");
            if (!TryText(fields, "q", out var query))
            {
                return null;
            }

            var scopes = fields.TryGetValue("scope", out var scope) ? ToStrings(scope) : ImmutableArray<string>.Empty;
            return new AxlMemoryQuery(query, TryInteger(fields, "lim", 8, 1, 10_000), scopes, id);
        }

        private AxlCommand? ParseSynthesis(AxlCommandId? id)
        {
            var fields = ParseFields("synth");
            var objective = fields.TryGetValue("obj", out var value) ? ToText(value) : "synthesize dependencies";
            return new AxlSynthesis(objective, TryReferences(fields, "dep"), id);
        }

        private AxlCommand? ParseVerify(AxlCommandId? id)
        {
            var fields = ParseFields("verify");
            var objective = fields.TryGetValue("obj", out var value) ? ToText(value) : "verify result";
            var profile = TryText(fields, "profile", out var parsedProfile) ? parsedProfile : null;
            return new AxlVerification(objective, TryReferences(fields, "dep"), profile, id);
        }

        private AxlCommand? ParseIntent(AxlCommandId? id)
        {
            var fields = ParseFields("intent");
            if (!TryText(fields, "obj", out var objective))
            {
                return null;
            }

            var priority = WorkPriority.Normal;
            if (fields.TryGetValue("pri", out var priorityValue) && !Enum.TryParse(ToText(priorityValue), true, out priority))
            {
                Add(AxlDiagnosticCode.InvalidValue, "pri must be a WorkPriority name.", Current.Span);
            }

            var attributes = fields.TryGetValue("attrs", out var attributeValue)
                ? ToStringMap(attributeValue)
                : ImmutableDictionary<string, string>.Empty;
            return new AxlIntent(objective, priority, attributes, id);
        }

        private AxlCommand? ParseDelegate(AxlCommandId? id)
        {
            var fields = ParseFields("delegate");
            var agent = TryReference(fields, "agent");
            if (agent is null || agent.Value.Kind != AxlReferenceKind.Agent || !TryText(fields, "obj", out var objective))
            {
                Add(AxlDiagnosticCode.MissingField, "delegate requires agent=@agent:<name> and obj=... .", Current.Span);
                return null;
            }

            var mode = TryText(fields, "mode", out var parsedMode) ? parsedMode : "readonly";
            return new AxlDelegation(agent.Value, objective, TryReferences(fields, "ev"), mode, id);
        }

        private AxlCommand? ParseResult(AxlCommandId? id)
        {
            var fields = ParseFields("ret");
            var correlation = TryReference(fields, "ref");
            var status = TryText(fields, "status", out var parsedStatus) ? parsedStatus : string.Empty;
            if (correlation is null || status is not ("ok" or "fail"))
            {
                Add(AxlDiagnosticCode.InvalidValue, "ret requires ref=<reference> and status=ok|fail.", Current.Span);
                return null;
            }

            var error = fields.TryGetValue("err", out var errorValue) ? ToText(errorValue) : null;
            return new AxlResult(correlation.Value, status == "ok", TryReferences(fields, "ev"), error, id);
        }

        private AxlCommand? ParseState(AxlCommandId? id)
        {
            var fields = ParseFields("state");
            var target = TryReference(fields, "ref");
            if (target is null || !TryText(fields, "status", out var status))
            {
                Add(AxlDiagnosticCode.MissingField, "state requires ref=<reference> and status=<state>.", Current.Span);
                return null;
            }

            return new AxlState(target.Value, status, id);
        }

        private AxlCommand? ParseSkill(AxlCommandId? id)
        {
            var fields = ParseFields("skill");
            if (!TryText(fields, "id", out var name) || !TryText(fields, "ver", out var version))
            {
                Add(AxlDiagnosticCode.MissingField, "skill requires id=<name> and ver=<version>.", Current.Span);
                return null;
            }

            var triggers = fields.TryGetValue("trigger", out var trigger) ? ToStrings(trigger) : ImmutableArray<string>.Empty;
            var requires = fields.TryGetValue("requires", out var required) ? ToStrings(required) : ImmutableArray<string>.Empty;
            var steps = fields.TryGetValue("steps", out var procedure) ? ToStrings(procedure) : ImmutableArray<string>.Empty;
            var verify = fields.TryGetValue("verify", out var verification) ? ToStrings(verification) : ImmutableArray<string>.Empty;
            var safety = TryText(fields, "safety", out var parsedSafety) ? parsedSafety : "readonly";
            return new AxlSkill(name, version, triggers, requires, steps, verify, safety, id);
        }

        private AxlCommand? UnknownCommand(string name)
        {
            Add(AxlDiagnosticCode.UnknownCommand, $"Unknown AXL command '{name}'.", Previous.Span, "known command", name);
            return null;
        }

        private Dictionary<string, AxlValue> ParseFields(string schemaName)
        {
            var fields = new Dictionary<string, AxlValue>(StringComparer.Ordinal);
            while (Current.Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.Equals)
            {
                var field = Current.Text;
                var fieldSpan = Current.Span;
                Move();
                Move();
                if (!fields.TryAdd(field, ParseValue()))
                {
                    Add(AxlDiagnosticCode.DuplicateField, $"Field '{field}' occurs more than once.", fieldSpan);
                }
            }

            if (!DefaultSchemas.TryGet(schemaName, out var schema))
            {
                return fields;
            }

            foreach (var field in fields.Keys)
            {
                if (!schema.Allows(field))
                {
                    Add(AxlDiagnosticCode.UnknownField, $"Field '{field}' is not valid for {schemaName}.", Current.Span);
                }
            }

            foreach (var required in schema.RequiredFields)
            {
                if (!fields.ContainsKey(required))
                {
                    Add(AxlDiagnosticCode.MissingField, $"Field '{required}' is required for {schemaName}.", Current.Span);
                }
            }

            return fields;
        }

        private AxlValue ParseValue()
        {
            if (Current.Kind == TokenKind.String)
            {
                var value = new AxlValue.Text(Current.Text);
                Move();
                return value;
            }

            if (Current.Kind == TokenKind.Number)
            {
                var text = Current.Text;
                Move();
                if (text.Contains('.', StringComparison.Ordinal) && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var decimalValue))
                {
                    return new AxlValue.DecimalValue(decimalValue);
                }

                if (text.Length > 0 && text[0] == '-' && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed))
                {
                    return new AxlValue.SignedInteger(signed);
                }

                if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unsigned))
                {
                    return new AxlValue.UnsignedInteger(unsigned);
                }

                Add(AxlDiagnosticCode.InvalidNumber, $"Invalid numeric literal '{text}'.", Previous.Span);
                return new AxlValue.Identifier(text);
            }

            if (Current.Kind == TokenKind.OpenBracket)
            {
                Move();
                var values = ImmutableArray.CreateBuilder<AxlValue>();
                while (Current.Kind is not (TokenKind.CloseBracket or TokenKind.End))
                {
                    if (values.Count >= _limits.MaxListItems)
                    {
                        Add(AxlDiagnosticCode.LimitExceeded, "AXL list exceeds the configured item limit.", Current.Span);
                        break;
                    }

                    values.Add(ParseValue());
                    _ = TryConsume(TokenKind.Comma, out _);
                }

                if (!Expect(TokenKind.CloseBracket, "']'"))
                {
                    Add(AxlDiagnosticCode.UnexpectedEnd, "AXL list is not closed.", Current.Span);
                }

                return new AxlValue.List(values.ToImmutable());
            }

            if (Current.Kind == TokenKind.OpenBrace)
            {
                Move();
                var fields = ImmutableArray.CreateBuilder<KeyValuePair<string, AxlValue>>();
                while (Current.Kind is not (TokenKind.CloseBrace or TokenKind.End))
                {
                    if (Current.Kind != TokenKind.Identifier || Peek(1).Kind != TokenKind.Equals)
                    {
                        Add(AxlDiagnosticCode.UnexpectedToken, "Record fields use key=value.", Current.Span);
                        break;
                    }

                    var key = Current.Text;
                    Move();
                    Move();
                    if (fields.Any(pair => pair.Key == key))
                    {
                        Add(AxlDiagnosticCode.DuplicateField, $"Record field '{key}' occurs more than once.", Previous.Span);
                    }

                    fields.Add(new(key, ParseValue()));
                    _ = TryConsume(TokenKind.Comma, out _);
                }

                Expect(TokenKind.CloseBrace, "'}'");
                return new AxlValue.Record(fields.ToImmutable());
            }

            if (Current.Kind == TokenKind.Identifier)
            {
                var text = Current.Text;
                Move();
                if (text is "true" or "false")
                {
                    return new AxlValue.BooleanValue(text == "true");
                }

                if (text == "null")
                {
                    return new AxlValue.Null();
                }

                return AxlReference.TryParse(text, out var reference)
                    ? new AxlValue.ReferenceValue(reference)
                    : new AxlValue.Identifier(text);
            }

            Add(AxlDiagnosticCode.UnexpectedEnd, "A value is required.", Current.Span);
            return new AxlValue.Null();
        }

        private bool TryText(Dictionary<string, AxlValue> fields, string name, out string value)
        {
            if (fields.TryGetValue(name, out var field))
            {
                value = ToText(field);
                if (value.Length > 0)
                {
                    return true;
                }
            }

            value = string.Empty;
            if (name is "q" or "obj")
            {
                Add(AxlDiagnosticCode.MissingField, $"Field '{name}' is required.", Current.Span);
            }

            return false;
        }

        private static AxlReference? TryReference(Dictionary<string, AxlValue> fields, string name) =>
            fields.TryGetValue(name, out var field) && field is AxlValue.ReferenceValue reference
                ? reference.Value
                : null;

        private ImmutableArray<AxlReference> TryReferences(Dictionary<string, AxlValue> fields, string name)
        {
            if (!fields.TryGetValue(name, out var value))
            {
                return ImmutableArray<AxlReference>.Empty;
            }

            var values = value is AxlValue.List list ? list.Values : ImmutableArray.Create(value);
            var references = ImmutableArray.CreateBuilder<AxlReference>(values.Length);
            foreach (var item in values)
            {
                if (item is AxlValue.ReferenceValue reference)
                {
                    references.Add(reference.Value);
                }
                else
                {
                    Add(AxlDiagnosticCode.InvalidReference, $"Field '{name}' must contain references.", Current.Span);
                }
            }

            return references.ToImmutable();
        }

        private int TryInteger(Dictionary<string, AxlValue> fields, string name, int fallback, int min, int max)
        {
            if (!fields.TryGetValue(name, out var value))
            {
                return fallback;
            }

            var integer = value switch
            {
                AxlValue.SignedInteger signed when signed.Value is >= int.MinValue and <= int.MaxValue => (int)signed.Value,
                AxlValue.UnsignedInteger unsigned when unsigned.Value <= int.MaxValue => (int)unsigned.Value,
                _ => int.MinValue
            };
            if (integer < min || integer > max)
            {
                Add(AxlDiagnosticCode.InvalidValue, $"Field '{name}' must be between {min} and {max}.", Current.Span);
                return fallback;
            }

            return integer;
        }

        private static bool TryBoolean(Dictionary<string, AxlValue> fields, string name, bool fallback) =>
            fields.TryGetValue(name, out var value) && value is AxlValue.BooleanValue boolean ? boolean.Value : fallback;

        private static string ToText(AxlValue value) => value switch
        {
            AxlValue.Text text => text.Value,
            AxlValue.Identifier identifier => identifier.Value,
            AxlValue.SignedInteger integer => integer.Value.ToString(CultureInfo.InvariantCulture),
            AxlValue.UnsignedInteger unsigned => unsigned.Value.ToString(CultureInfo.InvariantCulture),
            AxlValue.DecimalValue decimalValue => decimalValue.Value.ToString(CultureInfo.InvariantCulture),
            AxlValue.ReferenceValue reference => reference.Value.ToString(),
            AxlValue.BooleanValue boolean => boolean.Value ? "true" : "false",
            AxlValue.Null => "null",
            _ => string.Empty
        };

        private static ImmutableArray<string> ToStrings(AxlValue value)
        {
            var values = value is AxlValue.List list ? list.Values : ImmutableArray.Create(value);
            return values.Select(ToText).Where(static value => value.Length > 0).ToImmutableArray();
        }

        private static ImmutableDictionary<string, string> ToStringMap(AxlValue value) =>
            value is AxlValue.Record record
                ? record.Fields.ToImmutableDictionary(static pair => pair.Key, static pair => ToText(pair.Value), StringComparer.Ordinal)
                : ImmutableDictionary<string, string>.Empty;

        private Token Current => _tokens[Math.Min(_position, _tokens.Count - 1)];
        private Token Previous => _tokens[Math.Max(0, _position - 1)];
        private Token Peek(int offset) => _tokens[Math.Min(_position + offset, _tokens.Count - 1)];

        private void Move() => _position = Math.Min(_position + 1, _tokens.Count - 1);

        private bool TryConsume(TokenKind kind, out Token token)
        {
            if (Current.Kind == kind)
            {
                token = Current;
                Move();
                return true;
            }

            token = default;
            return false;
        }

        private bool TryConsumeIdentifier(string value)
        {
            if (Current.Kind == TokenKind.Identifier && Current.Text.Equals(value, StringComparison.Ordinal))
            {
                Move();
                return true;
            }

            return false;
        }

        private bool Expect(TokenKind kind, string expected)
        {
            if (TryConsume(kind, out _))
            {
                return true;
            }

            Add(Current.Kind == TokenKind.End ? AxlDiagnosticCode.UnexpectedEnd : AxlDiagnosticCode.UnexpectedToken, $"Expected {expected}.", Current.Span, expected, Current.Text);
            return false;
        }

        private void RecoverToCommandBoundary()
        {
            while (Current.Kind is not (TokenKind.End or TokenKind.CloseBrace))
            {
                if (Current.Kind == TokenKind.Identifier && (AxlCommandId.TryParse(Current.Text, out _) || IsCommandName(Current.Text)))
                {
                    return;
                }

                Move();
            }
        }

        private void Add(AxlDiagnosticCode code, string message, AxlSourceSpan span, string? expected = null, string? actual = null) =>
            _diagnostics.Add(new(AxlDiagnosticSeverity.Error, code, message, span, expected, actual));

        private AxlParseResult Finish(AxlParseStatus fallback) =>
            new(fallback, null, _diagnostics.ToImmutableArray());

        private static bool IsCommandName(string value) => value is "find" or "call" or "memory" or "synth" or "verify" or "intent" or "delegate" or "ret" or "state";
    }

}

internal static class AxlFieldExtensions
{
    public static ImmutableDictionary<string, AxlValue> ToParameters(this Dictionary<string, AxlValue> fields, IReadOnlySet<string> excluded)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, AxlValue>(StringComparer.Ordinal);
        foreach (var pair in fields)
        {
            if (!excluded.Contains(pair.Key))
            {
                builder[pair.Key] = pair.Value;
            }
        }

        return builder.ToImmutable();
    }
}
