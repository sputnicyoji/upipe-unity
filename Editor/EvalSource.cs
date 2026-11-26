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

            // Only the code moves: the comments and preprocessor lines before it stay in the body, which keeps the body's
            // #if/#endif balanced, and the moved text is wrapped in the conditions open where it was written.
            foreach (var chunk in scan.Chunks)
            {
                var first = FirstCode(code, chunk.Start, chunk.End);
                var bare = code.Substring(first, chunk.End - first);
                if (s_UsingDirective.IsMatch(StripComments(bare)))
                {
                    usings.Add(Conditioned(code, first, LineDirective(code, first) + StripComments(bare).Trim()));
                    Blank(body, first, chunk.End);
                }
                else if (s_TypeDeclaration.IsMatch(StripComments(bare)))
                {
                    types.Add(Conditioned(code, first, LineDirective(code, first) + new string(' ', Column(code, first)) + bare));
                    Blank(body, first, chunk.End);
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

        static void Blank(StringBuilder body, int start, int end)
        {
            for (var i = start; i < end; i++)
                if (body[i] != '\n')
                    body[i] = ' ';
        }

        // `text` inside the #if / #elif / #else lines still open at `position` (outermost first) and their #endifs.
        static string Conditioned(string code, int position, string text)
        {
            var open = new List<List<string>>();
            var i = 0;
            while (i < position)
            {
                var trivia = SkipTrivia(code, i);
                if (trivia > i)
                {
                    var directive = code[i] == '#' ? Regex.Match(code.Substring(i, trivia - i), @"^#\s*(\w+)").Groups[1].Value : "";
                    if (directive == "if")
                        open.Add(new List<string> { code.Substring(i, trivia - i).Trim() });
                    else if ((directive == "elif" || directive == "else") && open.Count > 0)
                        open[open.Count - 1].Add(code.Substring(i, trivia - i).Trim());
                    else if (directive == "endif" && open.Count > 0)
                        open.RemoveAt(open.Count - 1);
                    i = trivia;
                    continue;
                }
                var literal = SkipLiteral(code, i);
                i = literal > i ? literal : i + 1;
            }
            if (open.Count == 0)
                return text;
            return string.Concat(open.SelectMany(lines => lines).Select(line => line + "\n")) + text +
                string.Concat(Enumerable.Repeat("\n#endif", open.Count));
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
        internal static int SkipTrivia(string code, int i)
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
        internal static int SkipLiteral(string code, int i)
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
            "if", "for", "foreach", "while", "using", "lock", "switch", "catch", "fixed", "when",
        };

        static readonly HashSet<string> s_BlockKeywords = new HashSet<string>
        {
            "else", "try", "finally", "do", "unsafe", "checked", "unchecked",
        };

        static readonly HashSet<string> s_ExpressionPrefixes = new HashSet<string>
        {
            "new", "]", ">", "=", "(", ",", "return", "switch", "with", "?", ":", "??", "[", "await", "yield", "is",
        };

        static readonly HashSet<string> s_Continuations = new HashSet<string> { "else", "catch", "finally", "while" };

        // Splits the snippet into top-level statements and declarations, skipping literals and trivia, and
        // records what each '{' opens so that only statement and type bodies end a statement at their '}'.
        internal static Scan Split(string code)
        {
            var scan = new Scan();
            var braces = new Stack<Brace>();
            var parenOwners = new Stack<(string owner, bool afterNew)>();
            var tokens = new List<string>(); // significant tokens so far, for looking back
            (string owner, bool afterNew) lastClosedParen = ("", false);
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
                        parenOwners.Push((Last(tokens, 0), OpensObjectCreation(tokens)));
                        break;
                    case ')':
                        lastClosedParen = parenOwners.Count > 0 ? parenOwners.Pop() : ("", false);
                        break;
                    case '{':
                        braces.Push(Classify(code, start, i, tokens, lastClosedParen,
                            braces.Count > 0 ? braces.Peek() : Brace.Statement, parenOwners.Count > 0));
                        break;
                    case '}':
                        var closed = braces.Count > 0 ? braces.Pop() : Brace.Statement;
                        // Like ';', a '}' inside parentheses (a delegate passed to a call) never ends a statement, and
                        // nothing continues a type the way else/catch/finally/while continue a block.
                        if (braces.Count == 0 && parenOwners.Count == 0 && closed != Brace.Expression && closed != Brace.Lambda &&
                            (closed == Brace.Type || !s_Continuations.Contains(NextWord(code, i + 1))))
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

        static Brace Classify(string code, int chunkStart, int brace, List<string> tokens, (string owner, bool afterNew) lastClosedParen,
            Brace enclosing, bool inParentheses)
        {
            var first = FirstCode(code, chunkStart, brace);
            if (s_TypeDeclaration.IsMatch(StripComments(code.Substring(first, brace - first))))
                return Brace.Type;
            var previous = Last(tokens, 0);
            if (!inParentheses && IsGenericConstraintClause(tokens))
                return Brace.Function;
            if (previous == ")")
            {
                var (owner, afterNew) = lastClosedParen;
                if (s_StatementOwners.Contains(owner))
                    return Brace.Statement;
                if (owner == "delegate")
                    return Brace.Lambda; // delegate (int a) { ... }
                if (afterNew || owner == "]")
                    return Brace.Expression; // new A.Foo(1) { B = 2 }, new List<int>(1) { 2 }, new() { B = 2 }
                if (owner == ">" || IsWord(owner))
                    return Brace.Function;
                return Brace.Statement;
            }
            if (previous == "=>" || previous == "delegate")
                return Brace.Lambda;
            if (s_BlockKeywords.Contains(previous) || previous == "" || previous == ";" || previous == "}" || previous == "{")
                return Brace.Statement;
            if (previous == ":" && enclosing == Brace.Statement && !inParentheses)
                return Brace.Statement; // case 1: { ... } or a labelled block
            if (s_ExpressionPrefixes.Contains(previous) || IsPatternBrace(tokens) || enclosing == Brace.Expression)
                return Brace.Expression;
            if (IsWord(previous) && AfterNew(tokens, 0))
                return Brace.Expression; // new A.Foo { B = 1 }
            if (IsWord(previous))
                return Brace.Statement;
            return Brace.Expression;
        }

        // Whether the '(' about to open follows `new A.Foo`, `new A.Foo<T>`, or a target-typed `new`, so that a '{'
        // after its ')' is an initializer. `new()` after ':' or ',' is the constraint of a generic local function.
        static bool OpensObjectCreation(List<string> tokens)
        {
            var owner = Last(tokens, 0);
            if (owner == "new")
                return Last(tokens, 1) != ":" && Last(tokens, 1) != ",";
            return (owner == ">" || IsWord(owner)) && AfterNew(tokens, 0);
        }

        // Whether the type name ending `back` tokens from the end (with optional `<...>` and `.` qualifiers) follows `new`.
        static bool AfterNew(List<string> tokens, int back)
        {
            while (true)
            {
                if (Last(tokens, back) == ">")
                {
                    back = SkipBracketsBack(tokens, back, "<", ">");
                    if (back < 0)
                        return false;
                }
                if (!IsWord(Last(tokens, back)))
                    return false;
                if (Last(tokens, back + 1) == ".")
                {
                    back += 2;
                    continue;
                }
                return Last(tokens, back + 1) == "new";
            }
        }

        // Walks backward over balanced `open ... close` ending at `back` from the end, returning the index before `open`.
        static int SkipBracketsBack(List<string> tokens, int back, string open, string close)
        {
            var depth = 0;
            while (back < tokens.Count)
            {
                var token = Last(tokens, back);
                if (token == close)
                    depth++;
                else if (token == open && --depth == 0)
                    return back + 1;
                else if (token == ";" || token == "{" || token == "}" || token == "=>" || token == "=")
                    return -1;
                back++;
            }
            return -1;
        }

        // Generic local function constraint clause (`... ) where T : class/struct/Base/IComparable<T>/new() {`).
        static bool IsGenericConstraintClause(List<string> tokens)
        {
            var parenDepth = 0;
            for (var idx = tokens.Count - 1; idx >= 0; idx--)
            {
                var token = tokens[idx];
                if (token == ")")
                {
                    parenDepth++;
                    continue;
                }
                if (token == "(")
                {
                    if (parenDepth == 0)
                        return false;
                    parenDepth--;
                    continue;
                }
                if (parenDepth > 0)
                    continue;
                if (token == "{" || token == "}" || token == ";" || token == "=>" || token == "=" || token == "?")
                    return false;
                if (token == "where" && idx > 0 && tokens[idx - 1] == ")" &&
                    idx + 2 < tokens.Count && IsWord(tokens[idx + 1]) && tokens[idx + 2] == ":")
                    return true;
            }
            return false;
        }

        // Property pattern `{ ... }` in `s is { ... }`, `s is not { ... }`, `s is { } and/or { }`, `s is Ns.Type<T> { ... }`,
        // or `case Type { ... }:`.
        static bool IsPatternBrace(List<string> tokens)
        {
            var token = Last(tokens, 0);
            if (token == "is" || token == "and" || token == "or")
                return true;
            if (token == "not")
            {
                var prev = Last(tokens, 1);
                return prev == "is" || prev == "not" || prev == "and" || prev == "or" || prev == "case" || prev == "(";
            }
            if (token != ">" && !IsWord(token))
                return false;
            var back = 0;
            while (true)
            {
                if (Last(tokens, back) == ">")
                {
                    back = SkipBracketsBack(tokens, back, "<", ">");
                    if (back < 0)
                        return false;
                }
                if (!IsWord(Last(tokens, back)))
                    return false;
                back++;
                if (Last(tokens, back) == ".")
                {
                    back++;
                    continue;
                }
                break;
            }
            if (Last(tokens, back) == "not")
                back++;
            var keyword = Last(tokens, back);
            return keyword == "is" || keyword == "case" || keyword == "and" || keyword == "or";
        }

        static bool IsWord(string token) => token.Length > 0 && (char.IsLetter(token[0]) || token[0] == '_' || token[0] == '@');

        static string Last(List<string> tokens, int back) => tokens.Count > back ? tokens[tokens.Count - 1 - back] : "";

        internal static int SkipAllTrivia(string code, int i)
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
