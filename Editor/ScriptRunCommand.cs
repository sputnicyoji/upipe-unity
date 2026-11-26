using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Upipe.Editor
{
    // `script run`: compiles a .cs file (plus --extra-files) from anywhere on disk with the Editor's Roslyn
    // (CscCompiler), loads it from memory and calls a method on the main thread. Nothing is imported and the domain does
    // not reload. A returned awaitable (Task, UniTask, ...) is awaited through reflection, so no awaitable library is
    // referenced.
    internal static class ScriptRunCommand
    {
        const BindingFlags StaticMethods = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;
        const BindingFlags InstanceMethods = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        static readonly string[] s_DefaultEntries = { "Run", "Main" };
        static readonly char[] s_ListSeparators = { ';', ',' };
        static readonly char[] s_DefineSeparators = { ';', ',', ' ' };

        // Called on the main thread while the server starts, after ScriptEvalCommand set up CscCompiler.
        public static void Register()
        {
            CommandRegistry.Register(new CommandEntry
            {
                Group = "script",
                Name = "run",
                Alias = "script_run",
                Description = "Compile a .cs file (plus --extra-files) from anywhere on disk (no asset import, no domain reload) and call " +
                    "one of its methods. A returned Task, Task<T>, UniTask or other awaitable is awaited and its value is the result.",
                Tags = new[] { "scripts" },
                Parameters = new[]
                {
                    new CommandParameter { Name = "file", Required = true, Description = "Path to the main .cs file, absolute or relative to the project root." },
                    new CommandParameter { Name = "entry", Description = "Method to call: Type.Method or Namespace.Type.Method, in any of the compiled files. " +
                        "Default: the main file's only Run or Main. Static methods come first; an instance method runs on a new instance " +
                        "of a class with a parameterless constructor (not a UnityEngine.Object)." },
                    new CommandParameter { Name = "args", Description = "Arguments as JSON: the value itself for a one-parameter method, else an array (by position) or object (by name). CancellationToken and progress parameters (Action<string, int>, Action<string>, IProgress<string>, IProgress<int>, IProgress<(string, int)>) are passed for you; progress shows in unity job status for a --detach run." },
                    new CommandParameter { Name = "defines", Description = "Extra preprocessor symbols separated by ; or , (the Editor's script defines always apply)." },
                    new CommandParameter { Name = "timeout-ms", IsInteger = true, Description = "Timeout in milliseconds; it also cancels the method's CancellationToken." },
                    // Last, so positional binding of the parameters above stays as it was.
                    new CommandParameter { Name = "extra-files", Repeatable = true, Description = "More .cs files or directories (every .cs file below, " +
                        "hidden ones skipped) compiled with the main file. Separate paths with ; or , (or send a JSON array of paths)." },
                },
                MainThreadRequired = false,
                WaitsForSettle = true,
                DefaultTimeoutSeconds = 30,
                CancellableHandler = Run,
            });
        }

        // The context's token fires on the request timeout or a job cancel: this thread stops waiting and the method's
        // CancellationToken is cancelled; a method that ignores it keeps running on the main thread.
        static object Run(JObject parameters, CommandContext context)
        {
            var cancel = context.Cancel;
            var path = ScriptEvalCommand.ResolveScriptPath((string)parameters["file"]);
            var extraFiles = ResolveExtraFiles(CommandRegistry.ReadStrings(parameters, "extra-files", v => v.Split(s_ListSeparators)), path);
            var entry = (string)parameters["entry"];
            var args = ParseArgs(parameters["args"]);
            var defines = CscCompiler.EditorDefines.Concat(((string)parameters["defines"] ?? "").Split(s_DefineSeparators, StringSplitOptions.RemoveEmptyEntries));

            var files = new[] { path }.Concat(extraFiles).ToList();
            var sources = files.Select(file => new CscCompiler.SourceFile(file, File.ReadAllText(file))).ToList();
            // With helper files in the mix, a default Run or Main is looked for in the main file's own types only.
            var mainTypes = extraFiles.Count > 0 ? DeclaredTypeNames(sources[0].Content) : null;
            var name = "UpipeRun_" + CscCompiler.ShortHash(string.Join("\n", files));
            // 0436: a file that is also part of the project redefines the project's own types, which is fine here.
            var (assembly, symbols, errors) = CscCompiler.CompileFiles(name, sources, "0436", defines, cancel);
            if (errors.Count > 0)
                throw new CommandFailure("Command Execution Failed", $"COMPILATION_ERROR: Compilation Failed: {errors.Count} error(s)\n" +
                    string.Join("\n", errors.Select(e => DescribeError(e, files))));
            cancel.ThrowIfCancellationRequested();

            var running = (Task<object>)MainThreadQueue.RunAndWait(
                () => Start(assembly, symbols, entry, mainTypes, args, cancel, context.Progress), cancel, "INTERNAL_ERROR: could not start the script");
            // Each file was compiled under `#line 1 "<its path>"`, so frames already name the user's files.
            return ScriptEvalCommand.Await(running, cancel, "INTERNAL_ERROR");
        }

        // argv sends a string to parse; a structured request may send the JSON value itself.
        static JToken ParseArgs(JToken args)
        {
            if (args == null || args.Type == JTokenType.Null)
                return null;
            if (args.Type != JTokenType.String)
                return args;
            try
            {
                return JToken.Parse((string)args);
            }
            catch (JsonException e)
            {
                throw CommandFailure.InvalidUsage("--args must be JSON: " + e.Message);
            }
        }

        // --extra-files as full paths: directories become every non-hidden .cs file below them, in a stable order, and a
        // file named twice (or the main file again) is compiled once, compared the way the platform's file system does.
        // `values` are already split on ; and , (a path holding one comes in a JSON array).
        internal static List<string> ResolveExtraFiles(IEnumerable<string> values, string mainFile)
        {
            var seen = new HashSet<string>(UpipeServer.PathComparer) { UpipeServer.CanonicalizePath(mainFile) };
            var files = new List<string>();
            foreach (var item in values.Select(v => v.Trim()).Where(v => v.Length > 0))
            {
                var path = UpipeServer.ResolveProjectPath(item);
                IEnumerable<string> found;
                if (Directory.Exists(path))
                {
                    found = SourcesUnder(path).ToList();
                    if (!found.Any())
                        throw CommandFailure.InvalidUsage("--extra-files: no .cs file under " + path);
                }
                else if (!File.Exists(path) && !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    throw CommandFailure.InvalidUsage("--extra-files: directory or .cs file not found: " + path);
                else
                    found = new[] { ScriptEvalCommand.ResolveScriptPath(item, "--extra-files") };
                foreach (var file in found)
                    if (seen.Add(file))
                        files.Add(file);
            }
            return files;
        }

        static IEnumerable<string> SourcesUnder(string directory)
        {
            bool Visible(string path) => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal);
            var files = Directory.EnumerateFiles(directory)
                .Where(f => Visible(f) && f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal);
            var below = Directory.EnumerateDirectories(directory).Where(Visible).OrderBy(d => d, StringComparer.Ordinal).SelectMany(SourcesUnder);
            return files.Concat(below);
        }

        // As the official package prints them for one file (1-based line, 0-based column); an error in an extra file
        // starts with that file's name (its path when two compiled files share the name).
        static string DescribeError(CscCompiler.Error error, IReadOnlyList<string> files)
        {
            var text = error.LineColumnText;
            var comparer = UpipeServer.PathComparer;
            string file;
            try
            {
                file = UpipeServer.CanonicalizePath(Path.GetFullPath(error.File));
            }
            catch (Exception)
            {
                return error.Raw;
            }
            if (comparer.Equals(file, files[0]))
                return text;
            if (!files.Contains(file, comparer))
                return error.Raw;
            var fileName = Path.GetFileName(file);
            var shared = files.Count(f => comparer.Equals(Path.GetFileName(f), fileName)) > 1;
            return $"{(shared ? file : fileName)}: {text}";
        }

        // Full names (Namespace.Outer+Inner, generic arity as `N) of the types a C# file declares, read from its text
        // without compiling it: comments, strings and preprocessor lines are skipped and braces are tracked. Types
        // inside an inactive #if region are listed too, which is harmless since they are never compiled.
        // shortcut: both branches of an #if are scanned, so branches that open a different number of braces misplace
        // later types; the default entry is then not found and --entry still works. Evaluate the #if if that shows up.
        internal static HashSet<string> DeclaredTypeNames(string code)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            // The name prefix inside each open brace: "Ns." in a namespace, "Ns.Outer+" in a type, the enclosing one in
            // any other block.
            var prefixes = new Stack<string>();
            prefixes.Push("");
            string pendingNamespace = null, pendingType = null;
            var i = 0;
            while (i < code.Length)
            {
                var next = EvalSource.SkipTrivia(code, i);
                if (next == i)
                    next = EvalSource.SkipLiteral(code, i);
                if (next != i)
                {
                    i = next;
                    continue;
                }
                var c = code[i];
                if (c == '@' || IsIdentifierStart(c))
                {
                    var verbatim = c == '@';
                    var word = ReadIdentifier(code, ref i);
                    if (verbatim || pendingType != null)
                        continue;
                    if (word == "namespace")
                        pendingNamespace = ReadQualifiedName(code, ref i);
                    else if (word == "class" || word == "struct" || word == "interface" || word == "record")
                    {
                        var typeName = ReadQualifiedName(code, ref i);
                        if (word == "record" && (typeName == "class" || typeName == "struct"))
                            typeName = ReadQualifiedName(code, ref i);
                        // A constraint (`where T : class`) names no type: nothing or another clause follows it.
                        if (typeName.Length > 0 && typeName != "where" && typeName.IndexOf('.') < 0)
                            pendingType = typeName + Arity(code, ref i);
                    }
                    continue;
                }
                if (c == '{' || c == ';')
                {
                    var prefix = prefixes.Peek();
                    if (pendingType != null)
                    {
                        names.Add(prefix + pendingType);
                        if (c == '{')
                            prefixes.Push(prefix + pendingType + "+");
                    }
                    else if (pendingNamespace != null)
                        prefixes.Push(prefix + pendingNamespace + "."); // after `;`, a file-scoped namespace: to the end of the file
                    else if (c == '{')
                        prefixes.Push(prefix);
                    pendingNamespace = pendingType = null;
                }
                else if (c == '}' && prefixes.Count > 1)
                    prefixes.Pop();
                i++;
            }
            return names;
        }

        static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

        static string ReadIdentifier(string code, ref int i)
        {
            if (code[i] == '@')
                i++;
            var start = i;
            while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '_'))
                i++;
            return code.Substring(start, i - start);
        }

        // An identifier, or dotted ones (a namespace name), after trivia; empty when none follows.
        static string ReadQualifiedName(string code, ref int i)
        {
            var name = new StringBuilder();
            while (true)
            {
                i = EvalSource.SkipAllTrivia(code, i);
                if (i >= code.Length || !(code[i] == '@' || IsIdentifierStart(code[i])))
                    return name.ToString();
                name.Append(ReadIdentifier(code, ref i));
                var after = EvalSource.SkipAllTrivia(code, i);
                if (after >= code.Length || code[after] != '.')
                    return name.ToString();
                name.Append('.');
                i = after + 1;
            }
        }

        // `N for a generic declaration's type parameter list (its top-level commas plus one), else empty.
        static string Arity(string code, ref int i)
        {
            var at = EvalSource.SkipAllTrivia(code, i);
            if (at >= code.Length || code[at] != '<')
                return "";
            int depth = 0, count = 1;
            for (i = at; i < code.Length; i++)
            {
                if (code[i] == '<')
                    depth++;
                else if (code[i] == '>' && --depth == 0)
                {
                    i++;
                    break;
                }
                else if (code[i] == ',' && depth == 1)
                    count++;
            }
            return "`" + count;
        }

        // Main thread. A synchronous throw from the method (or its class's constructor) fails the returned task like an
        // asynchronous one.
        static Task<object> Start(byte[] assembly, byte[] symbols, string entry, ICollection<string> mainTypes, JToken args, CancellationToken cancel,
            Action<string, int?> progress)
        {
            var loaded = symbols != null ? Assembly.Load(assembly, symbols) : Assembly.Load(assembly);
            var method = FindEntry(loaded, entry, mainTypes);
            var arguments = BindArguments(method, args, cancel, progress);
            object value;
            try
            {
                var target = method.IsStatic ? null : Activator.CreateInstance(method.DeclaringType, nonPublic: true);
                value = method.Invoke(target, arguments);
            }
            catch (TargetInvocationException e)
            {
                return Task.FromException<object>(e.InnerException);
            }
            return Await(value, method.ReturnType);
        }

        // Static methods first: one is the entry, more are ambiguous. Only without any does an instance method count,
        // for legacy entry classes (`public void Main()`) that never needed a static one.
        static MethodInfo FindEntry(Assembly assembly, string entry, ICollection<string> mainTypes)
        {
            IEnumerable<Type> types = assembly.GetTypes();
            string methodName = null;
            if (string.IsNullOrEmpty(entry))
            {
                if (mainTypes != null)
                    types = types.Where(t => mainTypes.Contains(t.FullName));
            }
            else
            {
                var dot = entry.LastIndexOf('.');
                if (dot <= 0 || dot == entry.Length - 1)
                    throw CommandFailure.InvalidUsage($"--entry must be Type.Method or Namespace.Type.Method: {entry}");
                var typeName = entry.Substring(0, dot);
                methodName = entry.Substring(dot + 1);
                types = types.Where(t => t.FullName == typeName || t.FullName.Replace('+', '.') == typeName || t.Name == typeName);
            }
            var candidateTypes = types.ToList();
            bool Wanted(MethodInfo m) => methodName != null ? m.Name == methodName : s_DefaultEntries.Contains(m.Name);
            var wanted = methodName == null ? "Run or Main method" : "method " + entry;
            // --entry searches every compiled file; only the default entry stays in the main file.
            var where = methodName == null && mainTypes != null ? "the main file" : "the compiled files";

            var statics = candidateTypes.SelectMany(t => t.GetMethods(StaticMethods)).Where(m => Wanted(m) && !m.ContainsGenericParameters).ToList();
            if (statics.Count == 1)
                return statics[0];
            if (statics.Count > 1)
                throw CommandFailure.InvalidUsage($"More than one static {wanted} in {where} ({string.Join(", ", statics.Select(Describe))}); " +
                    "name one with --entry Type.Method.");

            // Abstract ones (an interface's `void Main();`) have no body to run; their implementations count instead.
            var instances = candidateTypes.SelectMany(t => t.GetMethods(InstanceMethods))
                .Where(m => Wanted(m) && !m.IsAbstract && !m.IsGenericMethodDefinition).ToList();
            if (instances.Count == 0)
                throw CommandFailure.InvalidUsage($"No static or instance {wanted} in {where}; name one with --entry Type.Method.");
            if (instances.Count > 1)
                throw CommandFailure.InvalidUsage($"More than one instance {wanted} in {where} ({string.Join(", ", instances.Select(Describe))}); " +
                    "name one with --entry Type.Method.");
            var problem = WhyNotConstructible(instances[0].DeclaringType);
            if (problem != null)
                throw CommandFailure.InvalidUsage($"{Describe(instances[0])} is an instance method, but {problem}. " +
                    "Make the method static, or declare it on a plain class with a parameterless constructor.");
            return instances[0];
        }

        // Why `new T()` cannot make the instance an instance entry runs on, or null when it can.
        internal static string WhyNotConstructible(Type type)
        {
            if (type.IsInterface || type.IsAbstract)
                return $"{type.FullName} is abstract";
            if (type.ContainsGenericParameters)
                return $"{type.FullName} is generic";
            if (typeof(UnityEngine.Object).IsAssignableFrom(type))
                return $"{type.FullName} is a UnityEngine.Object (MonoBehaviour, ScriptableObject, ...), which must not be created with new";
            if (!type.IsValueType && type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null) == null)
                return $"{type.FullName} has no parameterless constructor";
            return null;
        }

        static string Describe(MethodInfo method) =>
            $"{method.DeclaringType.FullName}.{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})";

        // One bindable parameter takes the whole JSON value; more take an array by position or an object by name.
        // CancellationToken parameters get the run's token and progress parameters the job's reporter; neither is bound.
        internal static object[] BindArguments(MethodInfo method, JToken args, CancellationToken cancel, Action<string, int?> progress = null)
        {
            var parameters = method.GetParameters();
            var values = new object[parameters.Length];
            var bindable = new List<ParameterInfo>();
            foreach (var p in parameters)
            {
                if (p.ParameterType == typeof(CancellationToken))
                    values[p.Position] = cancel;
                else if (ProgressArgument(p.ParameterType, progress) is object reporter)
                    values[p.Position] = reporter;
                else
                    bindable.Add(p);
            }

            var given = new Dictionary<ParameterInfo, JToken>();
            if (args != null)
            {
                if (bindable.Count == 0)
                    throw CommandFailure.InvalidUsage($"{Describe(method)} takes no arguments, but --args was given.");
                if (bindable.Count == 1)
                    given[bindable[0]] = args;
                else if (args is JArray items)
                {
                    if (items.Count > bindable.Count)
                        throw CommandFailure.InvalidUsage($"{Describe(method)} takes {bindable.Count} arguments; --args has {items.Count}.");
                    for (var i = 0; i < items.Count; i++)
                        given[bindable[i]] = items[i];
                }
                else if (args is JObject named)
                {
                    foreach (var property in named.Properties())
                    {
                        var parameter = bindable.FirstOrDefault(p => p.Name == property.Name);
                        if (parameter == null)
                            throw CommandFailure.InvalidUsage($"{Describe(method)} has no parameter '{property.Name}'.");
                        given[parameter] = property.Value;
                    }
                }
                else
                    throw CommandFailure.InvalidUsage($"{Describe(method)} takes {bindable.Count} arguments; give --args as a JSON array or object.");
            }

            foreach (var parameter in bindable)
            {
                if (!given.TryGetValue(parameter, out var token))
                {
                    if (!parameter.HasDefaultValue)
                        throw CommandFailure.InvalidUsage($"{Describe(method)}: no value for parameter '{parameter.Name}' in --args.");
                    values[parameter.Position] = parameter.DefaultValue;
                    continue;
                }
                try
                {
                    values[parameter.Position] = token.ToObject(parameter.ParameterType);
                }
                catch (Exception e) when (e is JsonException || e is ArgumentException || e is FormatException || e is InvalidCastException || e is OverflowException)
                {
                    throw CommandFailure.InvalidUsage($"--args: '{parameter.Name}' must be a {parameter.ParameterType.Name}: {e.Message}");
                }
            }
            return values;
        }

        // A progress parameter of a standard library type, wired to the job's progress (a no-op outside a job), so a script
        // reports progress without referencing this package: Action<string, int>, Action<string>, IProgress<string>,
        // IProgress<int> or IProgress<(string, int)>. Null for any other type.
        static object ProgressArgument(Type type, Action<string, int?> progress)
        {
            var report = progress ?? ((message, percent) => { });
            if (type == typeof(Action<string, int>))
                return new Action<string, int>((message, percent) => report(message, percent));
            if (type == typeof(Action<string>))
                return new Action<string>(message => report(message, null));
            if (type == typeof(IProgress<string>))
                return new Reporter<string>(message => report(message, null));
            if (type == typeof(IProgress<int>))
                return new Reporter<int>(percent => report(null, percent));
            if (type == typeof(IProgress<ValueTuple<string, int>>))
                return new Reporter<ValueTuple<string, int>>(value => report(value.Item1, value.Item2));
            return null;
        }

        // Unlike System.Progress<T>, reports at once on the caller's thread instead of posting to a SynchronizationContext.
        sealed class Reporter<T> : IProgress<T>
        {
            readonly Action<T> m_Report;
            public Reporter(Action<T> report) => m_Report = report;
            public void Report(T value) => m_Report(value);
        }

        // Awaits anything with GetAwaiter() whose awaiter has IsCompleted, OnCompleted(Action) and GetResult(): Task,
        // Task<T>, ValueTask, UniTask, UniTask<T>. Members come from the declared return type first: an `async Task`
        // method's task is a Task<VoidTaskResult> underneath, whose GetResult would hand back that placeholder.
        // The awaiter is boxed once and that box is used for every call, so a struct awaiter keeps its state.
        // Anything else is the result as it is.
        internal static Task<object> Await(object value, Type declaredType)
        {
            if (value == null)
                return Task.FromResult<object>(null);
            var getAwaiter = FindGetAwaiter(declaredType) ?? FindGetAwaiter(value.GetType());
            var awaiterType = getAwaiter?.ReturnType;
            var isCompleted = awaiterType?.GetProperty("IsCompleted", typeof(bool));
            var onCompleted = awaiterType?.GetMethod("OnCompleted", new[] { typeof(Action) });
            var getResult = awaiterType?.GetMethod("GetResult", Type.EmptyTypes);
            if (isCompleted == null || onCompleted == null || getResult == null)
                return Task.FromResult(value);

            var awaiter = getAwaiter.Invoke(value, null);
            var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action finish = () =>
            {
                try
                {
                    completion.TrySetResult(getResult.Invoke(awaiter, null)); // null for a void GetResult
                }
                catch (TargetInvocationException e)
                {
                    // Propagate exact exception type, message and stack trace (including user-thrown OperationCanceledException);
                    // ScriptEvalCommand.Await treats OperationCanceledException as cancellation only when its cancel token fired.
                    completion.TrySetException(e.InnerException ?? e);
                }
                catch (Exception e)
                {
                    completion.TrySetException(e);
                }
            };
            if ((bool)isCompleted.GetValue(awaiter))
                finish();
            else
                onCompleted.Invoke(awaiter, new object[] { finish });
            return completion.Task;
        }

        static MethodInfo FindGetAwaiter(Type type) =>
            type == null || type == typeof(object) ? null : type.GetMethod("GetAwaiter", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
    }
}
