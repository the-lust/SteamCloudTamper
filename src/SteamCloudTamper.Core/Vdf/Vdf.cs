using System.Text;

namespace SteamCloudTamper.Core.Vdf;

public sealed class VdfNode
{
    public string Key { get; set; } = "";
    public string? Value { get; set; }
    public List<VdfNode> Children { get; } = [];

    public bool IsValue => Value is not null;

    public VdfNode Add(string key, string value)
    {
        var n = new VdfNode { Key = key, Value = value };
        Children.Add(n);
        return n;
    }

    public VdfNode AddChild(string key)
    {
        var n = new VdfNode { Key = key };
        Children.Add(n);
        return n;
    }

    public VdfNode? this[string key] => Children.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
}

public static class VdfParser
{
    public static VdfNode Parse(ReadOnlySpan<char> text)
    {
        var pos = 0;
        var root = new VdfNode();
        ParseInto(text, ref pos, root);
        return root;
    }

    public static VdfNode ParseFile(string path) => Parse(File.ReadAllText(path));

    private static void ParseInto(ReadOnlySpan<char> text, ref int pos, VdfNode parent)
    {
        while (pos < text.Length)
        {
            SkipWhitespaceAndComments(text, ref pos);
            if (pos >= text.Length) break;

            var key = ReadString(text, ref pos);
            if (key is null) break;

            SkipWhitespaceAndComments(text, ref pos);
            if (pos >= text.Length) break;

            if (text[pos] == '{')
            {
                pos++;
                var child = parent.AddChild(key);
                ParseInto(text, ref pos, child);
            }
            else
            {
                var value = ReadString(text, ref pos);
                parent.Add(key, value ?? "");
            }
        }
    }

    private static string? ReadString(ReadOnlySpan<char> text, ref int pos)
    {
        SkipWhitespaceAndComments(text, ref pos);
        if (pos >= text.Length) return null;

        if (text[pos] == '"')
        {
            pos++;
            var sb = new StringBuilder();
            while (pos < text.Length)
            {
                var c = text[pos];
                if (c == '\\' && pos + 1 < text.Length)
                {
                    var next = text[pos + 1];
                    sb.Append(next switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        _ => next
                    });
                    pos += 2;
                }
                else if (c == '"')
                {
                    pos++;
                    return sb.ToString();
                }
                else
                {
                    sb.Append(c);
                    pos++;
                }
            }

            return sb.ToString();
        }

        if (text[pos] == '}')
        {
            pos++;
            return null;
        }

        var end = pos;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != '{' && text[end] != '}')
            end++;
        var token = text[pos..end].ToString();
        pos = end;
        return token;
    }

    private static void SkipWhitespaceAndComments(ReadOnlySpan<char> text, ref int pos)
    {
        while (pos < text.Length)
        {
            var c = text[pos];
            if (char.IsWhiteSpace(c))
            {
                pos++;
            }
            else if (c == '/' && pos + 1 < text.Length && text[pos + 1] == '/')
            {
                while (pos < text.Length && text[pos] != '\n') pos++;
            }
            else if (c == '/' && pos + 1 < text.Length && text[pos + 1] == '*')
            {
                pos += 2;
                while (pos + 1 < text.Length && !(text[pos] == '*' && text[pos + 1] == '/')) pos++;
                pos = Math.Min(pos + 2, text.Length);
            }
            else
            {
                break;
            }
        }
    }
}

public static class VdfWriter
{
    public static string Write(VdfNode root, int indent = 0)
    {
        var sb = new StringBuilder();
        foreach (var child in root.Children)
            WriteNode(child, 0, sb);
        return sb.ToString();
    }

    private static void WriteNode(VdfNode node, int depth, StringBuilder sb)
    {
        var pad = new string('\t', depth);
        sb.Append(pad).Append('"').Append(Escape(node.Key)).Append('"');

        if (node.IsValue)
        {
            sb.Append('\t').Append('"').Append(Escape(node.Value!)).Append('"').Append('\n');
        }
        else
        {
            sb.Append('\n').Append(pad).Append("{\n");
            foreach (var child in node.Children)
                WriteNode(child, depth + 1, sb);
            sb.Append(pad).Append("}\n");
        }
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t");
}