using System.Text;
using Ordoa.Core.Models;

namespace Ordoa.Core.Services;

/// <summary>
/// Small, dependency-free syntax highlighter for the preview panel. It is not a
/// compiler-grade lexer; it recognises comments, strings, numbers, keywords,
/// types and common operators for the languages Ordoa can preview.
/// </summary>
public static class SyntaxHighlighter
{
    private const int MaxLines = 4000;
    private const int MaxCharacters = 1024 * 1024;

    private enum Language
    {
        Plain,
        CLike,
        CSharp,
        Java,
        JavaScript,
        Python,
        Ruby,
        Shell,
        Sql,
        Json,
        Xml,
        Css,
        Go,
        Rust,
        Php,
        Lua,
    }

    private static readonly HashSet<string> SharedKeywords = new(StringComparer.Ordinal)
    {
        "if", "else", "for", "while", "do", "switch", "case", "default", "break", "continue",
        "return", "try", "catch", "finally", "throw", "new", "delete", "class", "interface",
        "enum", "struct", "public", "private", "protected", "internal", "static", "readonly",
        "const", "void", "var", "let", "function", "async", "await", "yield", "import", "export",
        "from", "package", "namespace", "using", "this", "super", "extends", "implements", "in",
        "of", "is", "as", "null", "true", "false", "undefined", "nil", "none", "and", "or",
        "not", "with", "pass", "raise", "lambda", "def", "elif", "except", "global", "nonlocal",
        "match", "when", "select", "where", "join", "group", "order", "by", "limit", "insert",
        "update", "create", "table", "into", "values", "set", "begin", "commit", "rollback",
        "fn", "pub", "mod", "use", "impl", "trait", "mut", "ref", "match", "loop", "unsafe",
        "defer", "go", "chan", "range", "type", "map", "fn", "fun", "val", "object", "companion",
        "override", "open", "override", "lateinit", "suspend", "sealed", "data", "when",
    };

    private static readonly HashSet<string> TypeKeywords = new(StringComparer.Ordinal)
    {
        "int", "long", "short", "byte", "char", "bool", "boolean", "float", "double", "decimal",
        "string", "object", "dynamic", "string", "str", "list", "dict", "set", "tuple", "array",
        "void", "any", "unknown", "never", "number", "bigint", "symbol", "Date", "Promise",
        "Task", "IEnumerable", "IReadOnlyList", "List", "Dictionary", "Span", "ReadOnlySpan",
        "u8", "u16", "u32", "u64", "i8", "i16", "i32", "i64", "f32", "f64", "usize", "isize",
        "uint", "ulong", "ushort", "sbyte", "nint", "nuint", "Nullable", "Guid", "DateTime",
    };

    public static CodePreview Highlight(string text, string extension)
    {
        var language = LanguageOf(extension);
        var lines = new List<CodeLine>();
        var plain = new StringBuilder(Math.Min(text.Length, MaxCharacters));
        var state = new LexerState();
        var truncated = false;

        using var reader = new StringReader(text);
        var number = 1;
        while (reader.ReadLine() is { } raw)
        {
            if (lines.Count >= MaxLines || plain.Length >= MaxCharacters)
            {
                truncated = true;
                break;
            }

            var spans = TokenizeLine(raw, language, state);
            lines.Add(new CodeLine(number++, raw, spans));
            plain.Append(raw).Append('\n');
        }

        return new CodePreview(lines, LanguageName(language), truncated, plain.ToString().TrimEnd('\n'));
    }

    public static string LanguageName(string extension) => LanguageName(LanguageOf(extension));

    private static string LanguageName(Language language) => language switch
    {
        Language.CSharp => "C#",
        Language.Java => "Java",
        Language.JavaScript => "JavaScript / TypeScript",
        Language.Python => "Python",
        Language.Ruby => "Ruby",
        Language.Shell => "Shell",
        Language.Sql => "SQL",
        Language.Json => "JSON",
        Language.Xml => "XML / HTML",
        Language.Css => "CSS",
        Language.Go => "Go",
        Language.Rust => "Rust",
        Language.Php => "PHP",
        Language.Lua => "Lua",
        _ => "Text",
    };

    private static IReadOnlyList<CodeSpan> TokenizeLine(string line, Language language, LexerState state)
    {
        var spans = new List<CodeSpan>();
        var plain = new StringBuilder();

        void Flush()
        {
            if (plain.Length == 0)
                return;
            spans.Add(new CodeSpan(plain.ToString(), CodeTokenKind.Text));
            plain.Clear();
        }

        void Add(string text, CodeTokenKind kind)
        {
            Flush();
            spans.Add(new CodeSpan(text, kind));
        }

        var index = 0;
        while (index < line.Length)
        {
            if (state.InBlockComment)
            {
                var end = line.IndexOf("*/", index, StringComparison.Ordinal);
                if (end < 0)
                {
                    Add(line[index..], CodeTokenKind.Comment);
                    return spans;
                }

                Add(line[index..(end + 2)], CodeTokenKind.Comment);
                index = end + 2;
                state.InBlockComment = false;
                continue;
            }

            var current = line[index];
            var next = index + 1 < line.Length ? line[index + 1] : '\0';

            if (language == Language.Xml && current == '<' && next == '!')
            {
                var end = line.IndexOf("-->", index, StringComparison.Ordinal);
                if (end >= 0)
                {
                    Add(line[index..(end + 3)], CodeTokenKind.Comment);
                    index = end + 3;
                    continue;
                }
            }

            if (IsLineCommentStart(language, current, next))
            {
                Add(line[index..], CodeTokenKind.Comment);
                break;
            }

            if (current == '/' && next == '*' && SupportsBlockComment(language))
            {
                var end = line.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (end >= 0)
                {
                    Add(line[index..(end + 2)], CodeTokenKind.Comment);
                    index = end + 2;
                    continue;
                }

                Add(line[index..], CodeTokenKind.Comment);
                state.InBlockComment = true;
                break;
            }

            if (current is '"' or '\'' or '`' && SupportsString(language, current))
            {
                var quote = current;
                var start = index++;
                var builder = new StringBuilder().Append(quote);
                var escaped = false;
                while (index < line.Length)
                {
                    var character = line[index];
                    builder.Append(character);
                    index++;
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }
                    if (character == '\\')
                    {
                        escaped = true;
                        continue;
                    }
                    if (character == quote)
                        break;
                }
                Add(builder.ToString(), CodeTokenKind.String);
                continue;
            }

            if (language == Language.Python && current == '#' ||
                language == Language.Shell && current == '#' ||
                language == Language.Ruby && current == '#' ||
                language == Language.Php && current == '#' && next == '#')
            {
                Add(line[index..], CodeTokenKind.Comment);
                break;
            }

            if (language == Language.Sql && current == '-' && next == '-')
            {
                Add(line[index..], CodeTokenKind.Comment);
                break;
            }

            if (char.IsDigit(current) ||
                (current == '.' && next is >= '0' and <= '9' && line.Length > index + 2))
            {
                var start = index;
                if (current == '0' && next is 'x' or 'X' or 'b' or 'B')
                {
                    index += 2;
                    while (index < line.Length && Uri.IsHexDigit(line[index]))
                        index++;
                }
                else
                {
                    while (index < line.Length && (char.IsDigit(line[index]) || line[index] is '.' or '_' or 'e' or 'E' or '+' or '-'))
                    {
                        if ((line[index] is '+' or '-') && index > start &&
                            line[index - 1] is not ('e' or 'E'))
                        {
                            break;
                        }
                        index++;
                    }
                }

                if (index < line.Length && line[index] is 'f' or 'F' or 'd' or 'D' or 'm' or 'M' or 'u' or 'U' or 'l' or 'L')
                    index++;
                Add(line[start..index], CodeTokenKind.Number);
                continue;
            }

            if (IsIdentifierStart(current, language))
            {
                var start = index++;
                while (index < line.Length && IsIdentifierPart(line[index], language))
                    index++;
                var word = line[start..index];
                var kind = ClassifyWord(word, language, line, index);
                Add(word, kind);
                continue;
            }

            if (current is '#' && language is Language.CLike or Language.CSharp or Language.Css)
            {
                Add(line[index..], CodeTokenKind.Preprocessor);
                break;
            }

            if (current is '@' && language == Language.CSharp)
            {
                var start = index++;
                while (index < line.Length && (char.IsLetterOrDigit(line[index]) || line[index] == '_'))
                    index++;
                Add(line[start..index], CodeTokenKind.Keyword);
                continue;
            }

            if (char.IsWhiteSpace(current))
            {
                plain.Append(current);
                index++;
                continue;
            }

            if ("+-*/%=<>!&|^~?:.".Contains(current))
                Add(current.ToString(), CodeTokenKind.Operator);
            else if ("{}[]();,".Contains(current))
                Add(current.ToString(), CodeTokenKind.Punctuation);
            else
                plain.Append(current);

            index++;
        }

        Flush();
        return spans;
    }

    private static CodeTokenKind ClassifyWord(string word, Language language, string line, int endIndex)
    {
        if (word.Length >= 2 && word[0] == '0' && (word[1] is 'x' or 'X'))
            return CodeTokenKind.Number;
        if (SharedKeywords.Contains(word))
            return CodeTokenKind.Keyword;
        if (TypeKeywords.Contains(word) || word.Length > 1 && char.IsUpper(word[0]))
            return CodeTokenKind.Type;

        var rest = endIndex < line.Length ? line[endIndex..].TrimStart() : string.Empty;
        if (rest.StartsWith('('))
            return CodeTokenKind.Function;
        return CodeTokenKind.Text;
    }

    private static bool SupportsBlockComment(Language language) => language is
        Language.CLike or Language.CSharp or Language.Java or Language.JavaScript or
        Language.Css or Language.Go or Language.Rust or Language.Php or Language.Sql;

    private static bool SupportsString(Language language, char quote) => language switch
    {
        Language.Python => quote is '"' or '\'',
        Language.Shell => quote is '"' or '\'',
        Language.Sql => quote is '"' or '\'',
        Language.Xml => quote is '"' or '\'',
        Language.Css => quote is '"' or '\'',
        _ => true,
    };

    private static bool IsLineCommentStart(Language language, char current, char next) => language switch
    {
        Language.CLike or Language.CSharp or Language.Java or Language.JavaScript or
            Language.Go or Language.Rust or Language.Php => current == '/' && next == '/',
        Language.Css => current == '/' && next == '*',
        _ => false,
    };

    private static bool IsIdentifierStart(char character, Language language)
        => char.IsLetter(character) || character == '_' || character == '$' ||
           language == Language.Css && character == '-';

    private static bool IsIdentifierPart(char character, Language language)
        => char.IsLetterOrDigit(character) || character == '_' || character == '$' ||
           language == Language.Css && character == '-';

    private static Language LanguageOf(string extension) => extension.ToLowerInvariant() switch
    {
        "cs" or "fs" or "vb" => Language.CSharp,
        "java" or "kt" or "kts" => Language.Java,
        "js" or "mjs" or "cjs" or "ts" or "tsx" or "jsx" or "vue" => Language.JavaScript,
        "py" or "pyw" => Language.Python,
        "rb" => Language.Ruby,
        "sh" or "bash" or "zsh" or "fish" or "ps1" or "bat" or "cmd" => Language.Shell,
        "sql" or "db" => Language.Sql,
        "json" or "jsonc" => Language.Json,
        "xml" or "html" or "htm" or "xaml" or "axaml" or "svg" => Language.Xml,
        "css" or "scss" or "less" => Language.Css,
        "go" => Language.Go,
        "rs" => Language.Rust,
        "php" => Language.Php,
        "lua" => Language.Lua,
        "c" or "h" or "cc" or "cpp" or "cxx" or "hpp" or "hxx" or "m" or "mm" or
            "swift" or "dart" or "scala" or "groovy" => Language.CLike,
        _ => Language.Plain,
    };

    private sealed class LexerState
    {
        public bool InBlockComment { get; set; }
    }
}
