using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Upipe.Editor
{
    // Turns a `script eval` snippet into one compilable file. `using` directives move to the top, type
    // declarations move into the snippet's own namespace, the statements become the body of an async method,
    // and a trailing expression becomes its return value. Moved text is replaced by spaces in place and the
    // generated parts carry `#line`, so diagnostics point at the line and column the caller wrote.
    internal static class EvalSource
    {
        public const string ScriptFileName = "script";
        // Lives in the snippet's own namespace; a name scripts are unlikely to declare themselves.
        public const string ClassName = "EvalEntry";

        public static bool HasTrailingExpression(string code)
        {
            var trailing = Split(code).Trailing;
            return trailing.End > trailing.Start;
        }

        static readonly string[] s_DefaultUsings =
        {
            "System", "System.Collections.Generic", "System.Linq", "System.Threading.Tasks", "UnityEngine", "UnityEditor",
        };

        // UnityEngine and System both declare these; scripts mean the Unity ones unless they alias them themselves.
        static readonly string[] s_UnityAliases = { "Object", "Random" };

        static readonly Regex s_UsingDirective = new Regex(@"^\s*using\s+(static\s+)?[A-Za-z_][\w.]*\s*(=\s*[^;(]+)?;\s*$");
        static readonly Regex s_TypeDeclaration = new Regex(
            @"^\s*(\[[^\]]*\]\s*)*((public|internal|private|protected|static|sealed|abstract|partial|readonly|unsafe|ref)\s+)*(class|struct|interface|enum|record|delegate)\s+");

        enum Brace
        {
            Statement,  // if/for/while/try bodies, bare blocks: the method's own flow
            Expression, // initializers, collection expressions, switch expressions
            Lambda,
            Function,   // local function body
            Type,
        }

        internal struct Chunk
        {
            public int Start;
            public int End; // exclusive
        }

        internal sealed class Scan
        {
            public readonly List<Chunk> Chunks = new List<Chunk>();
            public Chunk Trailing;
            // `return;` in the script's own flow: Run returns object, so these become `return null;`.
            public readonly List<int> BareReturns = new List<int>();
        }

        public static string Build(string code, string namespaceName, bool wrapTrailing = true)
        {
            var scan = Split(code);
            var body = new StringBuilder(code);
            var usings = new List<string>();
            var types = new List<string>();

            foreach (var chunk in scan.Chunks)
            {
                var text = code.Substring(chunk.Start, chunk.End - chunk.Start);
                var first = FirstCode(code, chunk.Start, chunk.End);
                var bare = code.Substring(first, chunk.End - first);
                if (s_UsingDirective.IsMatch(StripComments(bare)))
                {
                    usings.Add(LineDirective(code, first) + StripComments(bare).Trim());
                    Blank(body, chunk);
                }
                else if (s_TypeDeclaration.IsMatch(StripComments(bare)))
                {
                    types.Add(LineDirective(code, first) + new string(' ', Column(code, first)) + bare);
                    Blank(body, chunk);
                }
            }

            // Edits run from the end so earlier positions stay valid.
            var trailing = scan.Trailing;
            if (trailing.End > trailing.Start)
            {
                var first = FirstCode(code, trailing.Start, trailing.End);
                var expression = code.Substring(first, trailing.End - first);
                var replacement = wrapTrailing
                    // The expression keeps its own line and column; the wrapper sits on a line of its own.
                    ? "return (object)(\n" + LineDirective(code, first) + new string(' ', Column(code, first)) + expression + "\n);"
                    : expression + ";";
                body.Remove(first, trailing.End - first);
                body.Insert(first, replacement);
            }
            foreach (var position in scan.BareReturns.OrderByDescending(p => p))
                if (position < trailing.Start || trailing.End == trailing.Start)
                    body.Insert(position + "return".Length, " null");

            var source = new StringBuilder();
            foreach (var u in usings)
                source.Append(u).Append('\n');
            source.Append("#line default\n");
            foreach (var ns in s_DefaultUsings)
                source.Append("using ").Append(ns).Append(";\n");
            foreach (var alias in s_UnityAliases)
                if (!usings.Exists(u => Regex.IsMatch(u, @"^\s*using\s+" + alias + @"\s*=", RegexOptions.Multiline)))
                    source.Append("using ").Append(alias).Append(" = UnityEngine.").Append(alias).Append(";\n");
            // C# 9 records and init accessors need this type, which Unity's profile does not ship.
            source.Append("namespace System.Runtime.CompilerServices { internal static class IsExternalInit { } }\n");
            source.Append("namespace ").Append(namespaceName).Append("\n{\n");
            foreach (var t in types)
                source.Append(t).Append("\n#line default\n");
            source.Append("public static class ").Append(ClassName).Append("\n{\n");
            source.Append("public static async System.Threading.Tasks.Task<object> Run()\n{\n");
            source.Append("#line 1 \"").Append(ScriptFileName).Append("\"\n");
            source.Append(body).Append('\n');
            source.Append("#line default\nreturn null;\n}\n}\n}\n");
            return source.ToString();
        }

        static string LineDirective(string code, int position)
        {
            var line = 1;
            for (var i = 0; i < position; i++)
                if (code[i] == '\n')
                    line++;
            return "#line " + line + " \"" + ScriptFileName + "\"\n";
        }

        static int Column(string code, int position)
        {
            var lineStart = code.LastIndexOf('\n', Math.Max(0, position - 1)) + 1;
            return position < lineStart ? 0 : position - lineStart;
        }

        static void Blank(StringBuilder body, Chunk chunk)
        {
            for (var i = chunk.Start; i < chunk.End; i++)
                if (body[i] != '\n')
                    body[i] = ' ';
        }

        static string StripComments(string text)
        {
            var result = new StringBuilder();
            var i = 0;
            while (i < text.Length)
            {
                var skipped = SkipTrivia(text, i);
                if (skipped > i)
                {
                    result.Append(' ');
                    i = skipped;
                    continue;
                }
                var literal = SkipLiteral(text, i);
                var end = literal > i ? literal : i + 1;
                result.Append(text, i, end - i);
                i = end;
            }
            return result.ToString();
        }

        // First character of code in [start, end): past whitespace, comments and preprocessor lines.
        static int FirstCode(string code, int start, int end)
        {
            var i = start;
            while (i < end)
            {
                var next = SkipTrivia(code, i);
                if (next == i)
                    break;
                i = next;
            }
            return Math.Min(i, end);
        }

        // Past whitespace, a comment or a preprocessor line at `i`; returns `i` when there is none.
        static int SkipTrivia(string code, int i)
        {
            if (i >= code.Length)
                return i;
            var c = code[i];
            if (char.IsWhiteSpace(c))
            {
                while (i < code.Length && char.IsWhiteSpace(code[i]))
                    i++;
                return i;
            }
            var next = i + 1 < code.Length ? code[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                var end = code.IndexOf('\n', i);
                return end < 0 ? code.Length : end;
            }
            if (c == '/' && next == '*')
            {
                var close = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                return close < 0 ? code.Length : close + 2;
            }
            if (c == '#' && code.LastIndexOf('\n', Math.Max(0, i - 1)) is var lineStart && string.IsNullOrWhiteSpace(code.Substring(lineStart + 1, i - lineStart - 1)))
            {
                var end = code.IndexOf('\n', i);
                return end < 0 ? code.Length : end;
            }
            return i;
        }

        // Past a string or char literal starting at `i` ("", @"", $"", $@"", @$"", ''); returns `i` when there is none.
        static int SkipLiteral(string code, int i)
        {
            var j = i;
            var verbatim = false;
            var interpolated = false;
            while (j < code.Length && (code[j] == '@' || code[j] == '$') && j - i < 2)
            {
                verbatim |= code[j] == '@';
                interpolated |= code[j] == '$';
                j++;
            }
            if (j < code.Length && code[j] == '\'' && j == i)
                return SkipChar(code, i);
            if (j >= code.Length || code[j] != '"')
                return i;
            j++;
            while (j < code.Length)
            {
                var c = code[j];
                if (interpolated && c == '{')
                {
                    if (j + 1 < code.Length && code[j + 1] == '{')
                    {
                        j += 2;
                        continue;
                    }
                    j = SkipHole(code, j + 1);
                    continue;
                }
                if (verbatim)
                {
                    if (c == '"')
                    {
                        if (j + 1 < code.Length && code[j + 1] == '"')
                        {
                            j += 2;
                            continue;
                        }
                        return j + 1;
                    }
                }
                else
                {
                    if (c == '\\')
                    {
                        j += 2;
                        continue;
                    }
                    if (c == '"' || c == '\n')
                        return j + 1;
                }
                j++;
            }
            return code.Length;
        }

        // Past an interpolation hole's code, up to and including its closing '}'.
        static int SkipHole(string code, int i)
        {
            var depth = 0;
            while (i < code.Length)
            {
                var literal = SkipLiteral(code, i);
                if (literal > i)
                {
                    i = literal;
                    continue;
                }
                if (code[i] == '{')
                    depth++;
                else if (code[i] == '}')
                {
                    if (depth == 0)
                        return i + 1;
                    depth--;
                }
                i++;
            }
            return code.Length;
        }

        static int SkipChar(string code, int i)
        {
            i++;
            while (i < code.Length)
            {
                if (code[i] == '\\')
                {
                    i += 2;
                    continue;
                }
                if (code[i] == '\'' || code[i] == '\n')
                    return i + 1;
                i++;
            }
            return code.Length;
        }

        static readonly HashSet<string> s_StatementOwners = new HashSet<string>
        {
            "if", "for", "foreach", "while", "using", "lock", "switch", "catch", "fixed",
        };

        static readonly HashSet<string> s_BlockKeywords = new HashSet<string>
        {
            "else", "try", "finally", "do", "unsafe", "checked", "unchecked",
        };

        static readonly HashSet<string> s_ExpressionPrefixes = new HashSet<string>
        {
            "new", "]", ">", "=", "(", ",", "return", "switch", "with", "?", ":", "??", "[", "await", "yield",
        };

        static readonly HashSet<string> s_Continuations = new HashSet<string> { "else", "catch", "finally", "while" };

        // Splits the snippet into top-level statements and declarations, skipping literals and trivia, and
        // records what each '{' opens so that only statement and type bodies end a statement at their '}'.
        internal static Scan Split(string code)
        {
            var scan = new Scan();
            var braces = new Stack<Brace>();
            var parenOwners = new Stack<(string owner, string beforeOwner)>();
            var tokens = new List<string>(); // significant tokens so far, for looking back
            (string owner, string beforeOwner) lastClosedParen = ("", "");
            var start = 0;
            var i = 0;
            while (i < code.Length)
            {
                var trivia = SkipTrivia(code, i);
                if (trivia > i)
                {
                    i = trivia;
                    continue;
                }
                var literal = SkipLiteral(code, i);
                if (literal > i)
                {
                    tokens.Add("\"");
                    i = literal;
                    continue;
                }

                var c = code[i];
                if (char.IsLetter(c) || c == '_' || c == '@')
                {
                    var wordStart = i;
                    while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '_' || code[i] == '@'))
                        i++;
                    var word = code.Substring(wordStart, i - wordStart);
                    if (word == "return" && code.Length > SkipAllTrivia(code, i) && code[SkipAllTrivia(code, i)] == ';' &&
                        braces.All(b => b == Brace.Statement))
                        scan.BareReturns.Add(wordStart);
                    tokens.Add(word);
                    continue;
                }

                if (c == '=' && i + 1 < code.Length && code[i + 1] == '>')
                {
                    tokens.Add("=>");
                    i += 2;
                    continue;
                }
                if (c == '?' && i + 1 < code.Length && code[i + 1] == '?')
                {
                    tokens.Add("??");
                    i += 2;
                    continue;
                }

                switch (c)
                {
                    case '(':
                        parenOwners.Push((Last(tokens, 0), Last(tokens, 1)));
                        break;
                    case ')':
                        lastClosedParen = parenOwners.Count > 0 ? parenOwners.Pop() : ("", "");
                        break;
                    case '{':
                        braces.Push(Classify(code, start, i, tokens, lastClosedParen));
                        break;
                    case '}':
                        var closed = braces.Count > 0 ? braces.Pop() : Brace.Statement;
                        if (braces.Count == 0 && closed != Brace.Expression && closed != Brace.Lambda &&
                            !s_Continuations.Contains(NextWord(code, i + 1)))
                        {
                            scan.Chunks.Add(new Chunk { Start = start, End = i + 1 });
                            start = i + 1;
                            tokens.Clear();
                            tokens.Add("}");
                            i++;
                            continue;
                        }
                        break;
                    case ';':
                        if (braces.Count == 0 && parenOwners.Count == 0)
                        {
                            scan.Chunks.Add(new Chunk { Start = start, End = i + 1 });
                            start = i + 1;
                            tokens.Clear();
                            tokens.Add(";");
                            i++;
                            continue;
                        }
                        break;
                }
                tokens.Add(c.ToString());
                i++;
            }

            scan.Trailing = new Chunk { Start = start, End = start };
            if (FirstCode(code, start, code.Length) < code.Length)
                scan.Trailing = new Chunk { Start = start, End = TrailingEnd(code, start) };
            return scan;
        }

        static Brace Classify(string code, int chunkStart, int brace, List<string> tokens, (string owner, string beforeOwner) lastClosedParen)
        {
            var first = FirstCode(code, chunkStart, brace);
            if (s_TypeDeclaration.IsMatch(StripComments(code.Substring(first, brace - first))))
                return Brace.Type;
            var previous = Last(tokens, 0);
            if (previous == ")")
            {
                var (owner, beforeOwner) = lastClosedParen;
                if (s_StatementOwners.Contains(owner))
                    return Brace.Statement;
                if (beforeOwner == "new" || owner == ">" || owner == "]")
                    return Brace.Expression; // new Foo(1) { A = 2 }
                return IsWord(owner) ? Brace.Function : Brace.Statement;
            }
            if (previous == "=>")
                return Brace.Lambda;
            if (s_BlockKeywords.Contains(previous) || previous == "" || previous == ";" || previous == "}" || previous == "{")
                return Brace.Statement;
            if (s_ExpressionPrefixes.Contains(previous))
                return Brace.Expression;
            if (IsWord(previous) && Last(tokens, 1) == "new")
                return Brace.Expression; // new Foo { A = 1 }
            if (IsWord(previous))
                return Brace.Statement;
            return Brace.Expression;
        }

        static bool IsWord(string token) => token.Length > 0 && (char.IsLetter(token[0]) || token[0] == '_' || token[0] == '@');

        static string Last(List<string> tokens, int back) => tokens.Count > back ? tokens[tokens.Count - 1 - back] : "";

        static int SkipAllTrivia(string code, int i)
        {
            while (true)
            {
                var next = SkipTrivia(code, i);
                if (next == i)
                    return i;
                i = next;
            }
        }

        static string NextWord(string code, int from)
        {
            var i = SkipAllTrivia(code, from);
            var start = i;
            while (i < code.Length && (char.IsLetter(code[i]) || code[i] == '_'))
                i++;
            return code.Substring(start, i - start);
        }

        // End of the trailing expression: before a final line comment, so the comment stays outside the parentheses.
        static int TrailingEnd(string code, int start)
        {
            var end = code.Length;
            var i = start;
            var lastCode = start;
            while (i < code.Length)
            {
                var trivia = SkipTrivia(code, i);
                if (trivia > i)
                {
                    i = trivia;
                    continue;
                }
                var literal = SkipLiteral(code, i);
                i = literal > i ? literal : i + 1;
                lastCode = i;
            }
            return Math.Min(end, lastCode);
        }
    }
}
