namespace ShmViewer.Core.Model;

internal static class ConstantExpressionEvaluator
{
    public static bool TryEvaluate(string expression, TypeDatabase db, out long value)
        => TryEvaluate(expression, db, new HashSet<string>(StringComparer.Ordinal), out value);

    internal static bool TryEvaluate(string expression, TypeDatabase db, HashSet<string> visited, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var parser = new Parser(expression, db, visited);
        return parser.TryParse(out value);
    }

    private sealed class Parser
    {
        private readonly string _text;
        private readonly TypeDatabase _db;
        private readonly HashSet<string> _visited;
        private int _pos;

        public Parser(string text, TypeDatabase db, HashSet<string> visited)
        {
            _text = text;
            _db = db;
            _visited = visited;
        }

        public bool TryParse(out long value)
        {
            value = 0;
            SkipWhitespace();
            if (!ParseBitwiseOr(out value))
                return false;

            SkipWhitespace();
            return _pos == _text.Length;
        }

        private bool ParseBitwiseOr(out long value)
        {
            if (!ParseBitwiseXor(out value))
                return false;

            while (Match('|'))
            {
                if (!ParseBitwiseXor(out var rhs))
                    return false;
                value |= rhs;
            }

            return true;
        }

        private bool ParseBitwiseXor(out long value)
        {
            if (!ParseBitwiseAnd(out value))
                return false;

            while (Match('^'))
            {
                if (!ParseBitwiseAnd(out var rhs))
                    return false;
                value ^= rhs;
            }

            return true;
        }

        private bool ParseBitwiseAnd(out long value)
        {
            if (!ParseShift(out value))
                return false;

            while (Match('&'))
            {
                if (!ParseShift(out var rhs))
                    return false;
                value &= rhs;
            }

            return true;
        }

        private bool ParseShift(out long value)
        {
            if (!ParseAdditive(out value))
                return false;

            while (true)
            {
                if (Match("<<"))
                {
                    if (!ParseAdditive(out var rhs))
                        return false;
                    value <<= (int)rhs;
                }
                else if (Match(">>"))
                {
                    if (!ParseAdditive(out var rhs))
                        return false;
                    value >>= (int)rhs;
                }
                else
                {
                    break;
                }
            }

            return true;
        }

        private bool ParseAdditive(out long value)
        {
            if (!ParseMultiplicative(out value))
                return false;

            while (true)
            {
                if (Match('+'))
                {
                    if (!ParseMultiplicative(out var rhs))
                        return false;
                    value += rhs;
                }
                else if (Match('-'))
                {
                    if (!ParseMultiplicative(out var rhs))
                        return false;
                    value -= rhs;
                }
                else
                {
                    break;
                }
            }

            return true;
        }

        private bool ParseMultiplicative(out long value)
        {
            if (!ParseUnary(out value))
                return false;

            while (true)
            {
                if (Match('*'))
                {
                    if (!ParseUnary(out var rhs))
                        return false;
                    value *= rhs;
                }
                else if (Match('/'))
                {
                    if (!ParseUnary(out var rhs) || rhs == 0)
                        return false;
                    value /= rhs;
                }
                else if (Match('%'))
                {
                    if (!ParseUnary(out var rhs) || rhs == 0)
                        return false;
                    value %= rhs;
                }
                else
                {
                    break;
                }
            }

            return true;
        }

        private bool ParseUnary(out long value)
        {
            if (Match('+'))
                return ParseUnary(out value);

            if (Match('-'))
            {
                if (!ParseUnary(out value))
                    return false;
                value = -value;
                return true;
            }

            if (Match('~'))
            {
                if (!ParseUnary(out value))
                    return false;
                value = ~value;
                return true;
            }

            return ParsePrimary(out value);
        }

        private bool ParsePrimary(out long value)
        {
            value = 0;

            if (Match('('))
            {
                if (!ParseBitwiseOr(out value))
                    return false;
                return Match(')');
            }

            if (TryParseNumber(out value))
                return true;

            if (TryParseIdentifier(out var identifier))
                return _db.TryResolveConstantIdentifier(identifier, _visited, out value);

            return false;
        }

        private bool TryParseIdentifier(out string identifier)
        {
            identifier = string.Empty;
            SkipWhitespace();
            if (_pos >= _text.Length || !(char.IsLetter(_text[_pos]) || _text[_pos] == '_'))
                return false;

            int start = _pos++;
            while (_pos < _text.Length && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '_'))
                _pos++;

            identifier = _text[start.._pos];
            return true;
        }

        private bool TryParseNumber(out long value)
        {
            value = 0;
            SkipWhitespace();
            if (_pos >= _text.Length || !char.IsDigit(_text[_pos]))
                return false;

            int start = _pos++;
            while (_pos < _text.Length && IsLiteralChar(_text[_pos]))
                _pos++;

            var token = _text[start.._pos].TrimEnd('u', 'U', 'l', 'L');
            if (token.Length == 0)
                return false;

            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return long.TryParse(token[2..], System.Globalization.NumberStyles.HexNumber, null, out value);

            if (token.Length > 1 && token[0] == '0' && token.All(c => c is >= '0' and <= '7'))
            {
                try
                {
                    value = Convert.ToInt64(token, 8);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return long.TryParse(token, out value);
        }

        private static bool IsLiteralChar(char c)
            => char.IsLetterOrDigit(c) || c == '_';

        private bool Match(char ch)
        {
            SkipWhitespace();
            if (_pos >= _text.Length || _text[_pos] != ch)
                return false;

            _pos++;
            return true;
        }

        private bool Match(string token)
        {
            SkipWhitespace();
            if (_pos + token.Length > _text.Length)
                return false;

            if (!_text.AsSpan(_pos, token.Length).Equals(token, StringComparison.Ordinal))
                return false;

            _pos += token.Length;
            return true;
        }

        private void SkipWhitespace()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
                _pos++;
        }
    }
}
