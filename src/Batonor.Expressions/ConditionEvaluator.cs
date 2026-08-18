using System.Globalization;
using System.Text.Json.Nodes;
using Batonor.Abstractions;

namespace Batonor.Expressions;

/// <summary>
/// Hand-written recursive-descent evaluator for a small boolean/arithmetic expression language.
/// Supports <c>== != &lt; &gt; &lt;= &gt;= &amp;&amp; || ! + - * / %</c>, parentheses, string/number
/// literals, <c>true/false/null</c>, and <c>${path}</c> or bare variable references. No runtime code
/// generation — AOT-safe.
/// </summary>
public sealed class ConditionEvaluator : IExpressionEvaluator
{
    public bool Evaluate(string expression, IReadOnlyDictionary<string, JsonNode?> variables)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        var parser = new Parser(expression, variables);
        var result = parser.ParseExpression();
        return ToBool(result);
    }

    internal static bool ToBool(JsonNode? node)
    {
        if (node is null)
        {
            return false;
        }

        if (node is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b))
            {
                return b;
            }

            if (v.TryGetValue<string>(out var s))
            {
                return s.Length > 0;
            }

            if (v.TryGetValue<double>(out var d))
            {
                return d != 0;
            }
        }

        return true;
    }

    internal static double? ToNumber(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d))
            {
                return d;
            }

            if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    internal static string? ToText(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue v && v.TryGetValue<string>(out var s))
        {
            return s;
        }

        return node.ToJsonString();
    }

    internal enum TokenKind
    {
        Number, String, Identifier, Variable,
        Plus, Minus, Star, Slash, Percent,
        Eq, NotEq, Lt, Gt, LtEq, GtEq,
        And, Or, Not,
        LParen, RParen, End,
    }

    internal readonly record struct Token(TokenKind Kind, string Text, int Position);

    private sealed class Lexer
    {
        private readonly string _src;
        private int _pos;

        public Lexer(string src) => _src = src;

        public List<Token> Tokenize()
        {
            var tokens = new List<Token>();
            while (true)
            {
                SkipWhitespace();
                if (_pos >= _src.Length)
                {
                    tokens.Add(new Token(TokenKind.End, "", _pos));
                    return tokens;
                }

                var start = _pos;
                var c = _src[_pos];

                switch (c)
                {
                    case '+': tokens.Add(new Token(TokenKind.Plus, "+", _pos)); _pos++; break;
                    case '-': tokens.Add(new Token(TokenKind.Minus, "-", _pos)); _pos++; break;
                    case '*': tokens.Add(new Token(TokenKind.Star, "*", _pos)); _pos++; break;
                    case '/': tokens.Add(new Token(TokenKind.Slash, "/", _pos)); _pos++; break;
                    case '%': tokens.Add(new Token(TokenKind.Percent, "%", _pos)); _pos++; break;
                    case '(': tokens.Add(new Token(TokenKind.LParen, "(", _pos)); _pos++; break;
                    case ')': tokens.Add(new Token(TokenKind.RParen, ")", _pos)); _pos++; break;

                    case '!':
                        if (Peek(1) == '=') { tokens.Add(new Token(TokenKind.NotEq, "!=", _pos)); _pos += 2; }
                        else { tokens.Add(new Token(TokenKind.Not, "!", _pos)); _pos++; }
                        break;

                    case '=':
                        if (Peek(1) == '=') { tokens.Add(new Token(TokenKind.Eq, "==", _pos)); _pos += 2; }
                        else throw Error("Expected '=='.");
                        break;

                    case '<':
                        if (Peek(1) == '=') { tokens.Add(new Token(TokenKind.LtEq, "<=", _pos)); _pos += 2; }
                        else { tokens.Add(new Token(TokenKind.Lt, "<", _pos)); _pos++; }
                        break;

                    case '>':
                        if (Peek(1) == '=') { tokens.Add(new Token(TokenKind.GtEq, ">=", _pos)); _pos += 2; }
                        else { tokens.Add(new Token(TokenKind.Gt, ">", _pos)); _pos++; }
                        break;

                    case '&':
                        if (Peek(1) == '&') { tokens.Add(new Token(TokenKind.And, "&&", _pos)); _pos += 2; }
                        else throw Error("Expected '&&'.");
                        break;

                    case '|':
                        if (Peek(1) == '|') { tokens.Add(new Token(TokenKind.Or, "||", _pos)); _pos += 2; }
                        else throw Error("Expected '||'.");
                        break;

                    case '$':
                        tokens.Add(ReadVariable());
                        break;

                    case '\'':
                    case '"':
                        tokens.Add(ReadString(c));
                        break;

                    default:
                        if (char.IsDigit(c))
                        {
                            tokens.Add(ReadNumber());
                        }
                        else if (char.IsLetter(c) || c == '_')
                        {
                            tokens.Add(ReadIdentifier());
                        }
                        else
                        {
                            throw new BatonorException($"Unexpected character '{c}' at position {_pos}.");
                        }
                        break;
                }
            }
        }

        private char Peek(int ahead) => _pos + ahead < _src.Length ? _src[_pos + ahead] : '\0';

        private Token ReadVariable()
        {
            var start = _pos; // at '$'
            _pos++; // consume '$'
            if (Peek(0) != '{')
            {
                throw Error("Expected '${'.");
            }

            _pos++; // consume '{'
            var nameStart = _pos;
            while (_pos < _src.Length && _src[_pos] != '}')
            {
                _pos++;
            }

            if (_pos >= _src.Length)
            {
                throw Error("Unclosed '${'.");
            }

            var path = _src.Substring(nameStart, _pos - nameStart).Trim();
            _pos++; // consume '}'
            return new Token(TokenKind.Variable, path, start);
        }

        private Token ReadString(char quote)
        {
            var start = _pos;
            _pos++; // consume opening quote
            var sb = new System.Text.StringBuilder();
            while (_pos < _src.Length && _src[_pos] != quote)
            {
                if (_src[_pos] == '\\' && _pos + 1 < _src.Length)
                {
                    _pos++;
                }

                sb.Append(_src[_pos]);
                _pos++;
            }

            if (_pos >= _src.Length)
            {
                throw Error("Unclosed string literal.");
            }

            _pos++; // consume closing quote
            return new Token(TokenKind.String, sb.ToString(), start);
        }

        private Token ReadNumber()
        {
            var start = _pos;
            while (_pos < _src.Length && (char.IsDigit(_src[_pos]) || _src[_pos] == '.'))
            {
                _pos++;
            }

            return new Token(TokenKind.Number, _src.Substring(start, _pos - start), start);
        }

        private Token ReadIdentifier()
        {
            var start = _pos;
            while (_pos < _src.Length && (char.IsLetterOrDigit(_src[_pos]) || _src[_pos] == '_' || _src[_pos] == '.'))
            {
                _pos++;
            }

            return new Token(TokenKind.Identifier, _src.Substring(start, _pos - start), start);
        }

        private void SkipWhitespace()
        {
            while (_pos < _src.Length && char.IsWhiteSpace(_src[_pos]))
            {
                _pos++;
            }
        }

        private BatonorException Error(string message) =>
            new($"Expression parse error at position {_pos}: {message}");
    }

    private sealed class Parser
    {
        private readonly IReadOnlyDictionary<string, JsonNode?> _scope;
        private readonly List<Token> _tokens;
        private int _index;

        public Parser(string src, IReadOnlyDictionary<string, JsonNode?> scope)
        {
            _scope = scope;
            _tokens = new Lexer(src).Tokenize();
        }

        public JsonNode? ParseExpression() => ParseOr();

        private JsonNode? ParseOr()
        {
            var left = ParseAnd();
            while (Match(TokenKind.Or))
            {
                var right = ParseAnd();
                // Short-circuit: left || right
                left = ToBool(left) ? JsonValue.Create(true) : ToBool(right) ? JsonValue.Create(true) : JsonValue.Create(false);
            }

            return left;
        }

        private JsonNode? ParseAnd()
        {
            var left = ParseCompare();
            while (Match(TokenKind.And))
            {
                var right = ParseCompare();
                left = ToBool(left) && ToBool(right) ? JsonValue.Create(true) : JsonValue.Create(false);
            }

            return left;
        }

        private JsonNode? ParseCompare()
        {
            var left = ParseAdd();
            while (true)
            {
                if (Match(TokenKind.Eq)) { var right = ParseAdd(); left = JsonValue.Create(CompareEq(left, right)); }
                else if (Match(TokenKind.NotEq)) { var right = ParseAdd(); left = JsonValue.Create(!CompareEq(left, right)); }
                else if (Match(TokenKind.Lt)) { var right = ParseAdd(); left = JsonValue.Create(Compare(left, right) < 0); }
                else if (Match(TokenKind.Gt)) { var right = ParseAdd(); left = JsonValue.Create(Compare(left, right) > 0); }
                else if (Match(TokenKind.LtEq)) { var right = ParseAdd(); left = JsonValue.Create(Compare(left, right) <= 0); }
                else if (Match(TokenKind.GtEq)) { var right = ParseAdd(); left = JsonValue.Create(Compare(left, right) >= 0); }
                else return left;
            }
        }

        private static bool CompareEq(JsonNode? a, JsonNode? b)
        {
            if (a is null && b is null) return true;
            if (a is null || b is null) return false;
            var na = ToNumber(a);
            var nb = ToNumber(b);
            if (na.HasValue && nb.HasValue) return na.Value == nb.Value;
            return string.Equals(ToText(a), ToText(b), StringComparison.Ordinal);
        }

        private static int Compare(JsonNode? a, JsonNode? b)
        {
            var na = ToNumber(a);
            var nb = ToNumber(b);
            if (na.HasValue && nb.HasValue)
            {
                return na.Value.CompareTo(nb.Value);
            }

            return string.CompareOrdinal(ToText(a), ToText(b));
        }

        private JsonNode? ParseAdd()
        {
            var left = ParseMul();
            while (true)
            {
                if (Match(TokenKind.Plus)) { var right = ParseMul(); left = Arithmetic(left, right, (x, y) => x + y); }
                else if (Match(TokenKind.Minus)) { var right = ParseMul(); left = Arithmetic(left, right, (x, y) => x - y); }
                else return left;
            }
        }

        private JsonNode? ParseMul()
        {
            var left = ParseUnary();
            while (true)
            {
                if (Match(TokenKind.Star)) { var right = ParseUnary(); left = Arithmetic(left, right, (x, y) => x * y); }
                else if (Match(TokenKind.Slash)) { var right = ParseUnary(); left = Arithmetic(left, right, (x, y) => x / y); }
                else if (Match(TokenKind.Percent)) { var right = ParseUnary(); left = Arithmetic(left, right, (x, y) => x % y); }
                else return left;
            }
        }

        private JsonNode? ParseUnary()
        {
            if (Match(TokenKind.Not))
            {
                return JsonValue.Create(!ToBool(ParseUnary()));
            }

            if (Match(TokenKind.Minus))
            {
                var operand = ToNumber(ParseUnary());
                return operand.HasValue ? JsonValue.Create(-operand.Value) : null;
            }

            return ParsePrimary();
        }

        private JsonNode? ParsePrimary()
        {
            if (Match(TokenKind.Number))
            {
                var text = Previous().Text;
                return JsonValue.Create(double.Parse(text, CultureInfo.InvariantCulture));
            }

            if (Match(TokenKind.String))
            {
                return JsonValue.Create(Previous().Text);
            }

            if (Match(TokenKind.Variable))
            {
                return ScopeResolver.Resolve(_scope, Previous().Text);
            }

            if (Match(TokenKind.Identifier))
            {
                var id = Previous().Text;
                return id switch
                {
                    "true" => JsonValue.Create(true),
                    "false" => JsonValue.Create(false),
                    "null" => null,
                    _ => ScopeResolver.Resolve(_scope, id),
                };
            }

            if (Match(TokenKind.LParen))
            {
                var expr = ParseOr();
                Expect(TokenKind.RParen, "Expected ')'.");
                return expr;
            }

            throw new BatonorException($"Unexpected token '{Current().Text}' at position {Current().Position}.");
        }

        private static JsonNode? Arithmetic(JsonNode? a, JsonNode? b, Func<double, double, double> op)
        {
            var x = ToNumber(a);
            var y = ToNumber(b);
            return x.HasValue && y.HasValue ? JsonValue.Create(op(x.Value, y.Value)) : null;
        }

        private bool Match(TokenKind kind)
        {
            if (Current().Kind != kind)
            {
                return false;
            }

            _index++;
            return true;
        }

        private void Expect(TokenKind kind, string message)
        {
            if (!Match(kind))
            {
                throw new BatonorException(message);
            }
        }

        private Token Current() => _tokens[Math.Min(_index, _tokens.Count - 1)];

        private Token Previous() => _tokens[Math.Max(0, _index - 1)];
    }
}
