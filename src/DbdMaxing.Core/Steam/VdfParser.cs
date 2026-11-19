using System.Text;

namespace DbdMaxing.Core.Steam;

/// <summary>
/// A node in a Valve KeyValues (VDF/ACF) tree. A node is either a leaf with a string
/// <see cref="Value"/> or an object with <see cref="Children"/>. Key lookups are case-insensitive,
/// matching Steam's own behaviour. Duplicate keys are preserved; the indexer returns the first match.
/// </summary>
public sealed class VdfNode
{
    private readonly List<VdfNode> _children = new();

    public VdfNode(string key, string? value = null)
    {
        Key = key;
        Value = value;
    }

    public string Key { get; }

    /// <summary>The string value for a leaf, or null for an object node.</summary>
    public string? Value { get; }

    public bool IsObject => Value is null;

    public IReadOnlyList<VdfNode> Children => _children;

    internal void Add(VdfNode child) => _children.Add(child);

    /// <summary>First child with the given key (case-insensitive), or null.</summary>
    public VdfNode? this[string key] =>
        _children.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Follows a path of keys (case-insensitive); null if any segment is missing.</summary>
    public VdfNode? Get(params string[] path)
    {
        VdfNode? node = this;
        foreach (var key in path)
        {
            node = node?[key];
            if (node is null) return null;
        }
        return node;
    }

    /// <summary>Value of the leaf at the given path, or null.</summary>
    public string? GetValue(params string[] path) => Get(path)?.Value;

    public override string ToString() => IsObject ? $"\"{Key}\" {{ {_children.Count} }}" : $"\"{Key}\" \"{Value}\"";
}

/// <summary>
/// Parser for Valve's text KeyValues format as used by Steam (loginusers.vdf, libraryfolders.vdf,
/// appmanifest_*.acf, localconfig.vdf). Supports quoted and unquoted tokens, nested braces,
/// backslash escapes (\n \t \r \\ \"), <c>//</c> line comments and conditional tags such as
/// <c>[$WIN32]</c> (which are ignored). Binary VDF (appinfo.vdf, packageinfo.vdf) is not supported.
/// </summary>
public static class VdfParser
{
    /// <summary>Parses VDF text. The returned root node (key "") holds the top-level pairs.</summary>
    /// <exception cref="FormatException">The text is malformed.</exception>
    public static VdfNode Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var reader = new Reader(text);
        var root = new VdfNode("");
        ParseObjectBody(reader, root, isRoot: true);
        return root;
    }

    /// <summary>Parses a file; returns null on any IO or format error.</summary>
    public static VdfNode? TryParseFile(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException
                                      or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Parses text; returns null if it is malformed.</summary>
    public static VdfNode? TryParse(string text)
    {
        try { return Parse(text); }
        catch (FormatException) { return null; }
    }

    private static void ParseObjectBody(Reader r, VdfNode parent, bool isRoot)
    {
        while (true)
        {
            var tok = r.Next();
            switch (tok.Kind)
            {
                case TokenKind.End:
                    if (!isRoot) throw new FormatException($"Unexpected end of input inside \"{parent.Key}\".");
                    return;
                case TokenKind.Close:
                    if (isRoot) throw new FormatException($"Unexpected '}}' at offset {tok.Offset}.");
                    return;
                case TokenKind.Open:
                    throw new FormatException($"Unexpected '{{' at offset {tok.Offset}; expected a key.");
                case TokenKind.String:
                    var key = tok.Text!;
                    var next = r.Next();
                    if (next.Kind == TokenKind.String)
                    {
                        parent.Add(new VdfNode(key, next.Text));
                    }
                    else if (next.Kind == TokenKind.Open)
                    {
                        var child = new VdfNode(key);
                        ParseObjectBody(r, child, isRoot: false);
                        parent.Add(child);
                    }
                    else
                    {
                        throw new FormatException($"Expected a value or '{{' after key \"{key}\" at offset {next.Offset}.");
                    }
                    break;
            }
        }
    }

    private enum TokenKind { String, Open, Close, End }

    private readonly record struct Token(TokenKind Kind, string? Text, int Offset);

    private sealed class Reader(string s)
    {
        private int _pos;

        public Token Next()
        {
            while (true)
            {
                SkipWhitespaceAndComments();
                if (_pos >= s.Length) return new Token(TokenKind.End, null, _pos);

                char c = s[_pos];
                int start = _pos;
                if (c == '{') { _pos++; return new Token(TokenKind.Open, null, start); }
                if (c == '}') { _pos++; return new Token(TokenKind.Close, null, start); }
                if (c == '[')
                {
                    // Conditional tag like [$WIN32] or [!$X360]; ignored.
                    int end = s.IndexOf(']', _pos);
                    if (end < 0) throw new FormatException($"Unterminated conditional at offset {start}.");
                    _pos = end + 1;
                    continue;
                }
                if (c == '"') return new Token(TokenKind.String, ReadQuoted(), start);
                return new Token(TokenKind.String, ReadUnquoted(), start);
            }
        }

        private void SkipWhitespaceAndComments()
        {
            while (_pos < s.Length)
            {
                char c = s[_pos];
                if (char.IsWhiteSpace(c) || c == '﻿') { _pos++; continue; }
                if (c == '/' && _pos + 1 < s.Length && s[_pos + 1] == '/')
                {
                    int nl = s.IndexOf('\n', _pos);
                    _pos = nl < 0 ? s.Length : nl + 1;
                    continue;
                }
                break;
            }
        }

        private string ReadQuoted()
        {
            int start = _pos;
            _pos++; // opening quote
            var sb = new StringBuilder();
            while (_pos < s.Length)
            {
                char c = s[_pos++];
                if (c == '"') return sb.ToString();
                if (c == '\\' && _pos < s.Length)
                {
                    char e = s[_pos++];
                    sb.Append(e switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        '\\' => '\\',
                        '"' => '"',
                        _ => e, // unknown escape: keep the character (Steam paths are pre-escaped as \\)
                    });
                    continue;
                }
                sb.Append(c);
            }
            throw new FormatException($"Unterminated string starting at offset {start}.");
        }

        private string ReadUnquoted()
        {
            int start = _pos;
            while (_pos < s.Length)
            {
                char c = s[_pos];
                if (char.IsWhiteSpace(c) || c is '{' or '}' or '"') break;
                if (c == '/' && _pos + 1 < s.Length && s[_pos + 1] == '/') break;
                _pos++;
            }
            return s.Substring(start, _pos - start);
        }
    }
}
