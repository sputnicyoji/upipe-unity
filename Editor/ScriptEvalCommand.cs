using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Upipe.Editor
{
    // `script eval`: compiles the snippet with the Roslyn compiler that ships with the Editor (the one Unity
    // compiles project scripts with) in a separate process, loads the assembly from memory and runs it on the
    // main thread. Compiling happens on the HTTP thread, so the main thread only pays for running the script.
    internal static class ScriptEvalCommand
    {
        const int MaxResultDepth = 4;
        const int MaxResultItems = 1000;
        const int MaxResultNodes = 10000;

        // Called on the main thread while the server starts.
        public static void Register()
        {
            CscCompiler.Initialize();

            CommandRegistry.Register(new CommandEntry
            {
                Group = "script",
                Name = "eval",
                Alias = "eval",
                Description = "Compile and run C#: statements, using directives, local functions and types, await. " +
                    "The value of `return x;` or of a trailing expression is the result.",
                Tags = new[] { "scripts" },
                Parameters = new[]
                {
                    new CommandParameter { Name = "code", Description = "C# code to evaluate. Give either code or file." },
                    new CommandParameter { Name = "file", Description = "Path to a .cs file to evaluate instead, relative to the project root." },
                    new CommandParameter { Name = "timeout", IsInteger = true, Description = "Timeout in seconds for the whole run (default 30)." },
                    new CommandParameter { Name = "dry-run", IsBool = true, Description = "Compile only and report diagnostics." },
                    // Last, so positional binding of the parameters above stays as it was.
                    new CommandParameter { Name = "timeout-ms", IsInteger = true, Description = "Timeout in milliseconds; used instead of timeout when both are given." },
                },
                MainThreadRequired = false,
                WaitsForSettle = true,
                DefaultTimeoutSeconds = 30,
                CancellableHandler = Eval,
            });
        }

        // `timedOut` is cancelled when the request times out: work that has not started is skipped and this
        // HTTP thread stops waiting, while a script already running keeps going on the main thread, as the docs say.
        static object Eval(JObject parameters, CancellationToken timedOut)
        {
            var code = (string)parameters["code"];
            var file = (string)parameters["file"];
            var dryRun = bool.TryParse(parameters["dry-run"]?.ToString(), out var flag) && flag;
            if (code != null && file != null)
                throw CommandFailure.InvalidUsage("Give either the code or --file, not both.");
            if (code == null && file == null)
                throw CommandFailure.InvalidUsage("Give the code to evaluate, or --file with a path to a .cs file");
            if (file != null)
                code = File.ReadAllText(ResolveScriptPath(file));
            if (string.IsNullOrWhiteSpace(code))
                throw CommandFailure.InvalidUsage("The code to evaluate is empty.");

            var namespaceName = "UpipeEval_" + Guid.NewGuid().ToString("N");
            var (assembly, symbols) = CompileScript(code, namespaceName);
            if (dryRun)
                return null;
            timedOut.ThrowIfCancellationRequested();

            Task<object> script;
            try
            {
                script = (Task<object>)MainThreadQueue.Run(() => Start(assembly, symbols, namespaceName + "." + EvalSource.ClassName), timedOut).Result;
            }
            catch (AggregateException e)
            {
                var error = UpipeServer.Unwrap(e);
                if (error is EditorReloadingException || error is OperationCanceledException)
                    throw error;
                throw new CommandFailure("Command Execution Failed", "EVAL_INTERNAL_ERROR: " + error.Message);
            }

            try
            {
                // Blocks this HTTP thread only; the script's awaits resume on the main thread.
                script.Wait(timedOut);
            }
            catch (AggregateException e)
            {
                var error = UpipeServer.Unwrap(e);
                // Frames in the script read "in script:LINE" rather than the temporary file's full path.
                var trace = (error.StackTrace ?? "").Replace(CscCompiler.WorkDirectory + Path.DirectorySeparatorChar, "");
                throw new CommandFailure("Command Execution Failed",
                    $"RUNTIME_EXCEPTION: Runtime Error. {error.GetType().Name}: {error.Message}\n{trace}");
            }
            var value = script.Result;
            try
            {
                // Reading a Unity object (even its name) is main-thread only.
                return MainThreadQueue.Run(() => ToJson(value, 0, new Budget()), timedOut).Result;
            }
            catch (AggregateException e)
            {
                var error = UpipeServer.Unwrap(e);
                // Not the retryable 503: the script has run (it may have asked for this very reload), and a client
                // that resends would run it again.
                if (error is EditorReloadingException)
                    throw new CommandFailure("Command Execution Failed",
                        "The script ran, but a domain reload started before its result could be read.");
                if (error is OperationCanceledException)
                    throw error;
                throw new CommandFailure("Command Execution Failed", "EVAL_INTERNAL_ERROR: could not convert the result: " + error.Message);
            }
        }

        // A trailing expression is returned as the result; when it turns out to be a void call (Debug.Log("x")),
        // compile it again as a plain statement, which returns null.
        static (byte[] assembly, byte[] symbols) CompileScript(string code, string namespaceName)
        {
            try
            {
                return Compile(namespaceName, EvalSource.Build(code, namespaceName));
            }
            catch (CommandFailure failure) when (failure.Message.Contains("error CS0030: Cannot convert type 'void' to 'object'") &&
                                                 EvalSource.HasTrailingExpression(code))
            {
                return Compile(namespaceName, EvalSource.Build(code, namespaceName, wrapTrailing: false));
            }
        }

        // Absolute, or relative to the project root; also used by script run.
        internal static string ResolveScriptPath(string file)
        {
            var path = UpipeServer.ResolveProjectPath(file);
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                throw CommandFailure.InvalidUsage("--file must name a .cs file: " + path);
            if (!File.Exists(path))
                throw CommandFailure.InvalidUsage("File not found: " + path);
            return path;
        }

        static object Start(byte[] assembly, byte[] symbols, string typeName)
        {
            var loaded = symbols != null ? Assembly.Load(assembly, symbols) : Assembly.Load(assembly);
            return loaded.GetType(typeName).GetMethod("Run").Invoke(null, null);
        }

        static (byte[] assembly, byte[] symbols) Compile(string name, string source)
        {
            // async method without await, the unreachable `return null` after a script's own return, duplicate usings
            var (assembly, symbols, errors) = CscCompiler.Compile(name, source, "1998,0162,0105");
            if (errors.Count == 0)
                return (assembly, symbols);
            // Errors in the user's lines read as the official package prints them (1-based line, 0-based column);
            // csc prints the #line file relative to its own folder. Errors in the generated wrapper stay raw.
            var lines = errors.Select(e => Path.GetFileName(e.File) == EvalSource.ScriptFileName
                ? $"line {e.Line}, col {e.Column - 1}: error {e.Id}: {e.Message}"
                : e.Raw).ToList();
            throw new CommandFailure("Command Execution Failed",
                $"COMPILATION_ERROR: Compilation Failed: {lines.Count} error(s)\n" + string.Join("\n", lines));
        }

        // Caps the total work of converting one result; past it values read as their ToString().
        sealed class Budget
        {
            public int Remaining = MaxResultNodes;
        }

        internal static object ToJson(object value) => ToJson(value, 0, new Budget());

        // The script's value as JSON. Unity objects and Unity value types read as their ToString(), as the
        // official package returns them. Collections become arrays and objects, and other objects are read
        // through their public fields and properties. Framework types (System.*: Task, Lazy, Type, Stream...)
        // are never read that way: their getters can block the main thread (Task.Result) or explode in size.
        // Depth, items per collection and total nodes are capped, so no value can recurse or run forever.
        static object ToJson(object value, int depth, Budget budget)
        {
            if (value == null)
                return null;
            if (value is UnityEngine.Object unityObject)
                return unityObject == null ? null : unityObject.ToString(); // a destroyed object compares equal to null
            var type = value.GetType();
            if (value is string || value is bool || value is decimal || type.IsPrimitive)
                return JToken.FromObject(value);
            if (type.IsEnum || depth >= MaxResultDepth || --budget.Remaining <= 0 || IsUnityType(type))
                return value.ToString();
            if (value is IDictionary dictionary)
            {
                var map = new JObject();
                foreach (var pair in dictionary.Cast<DictionaryEntry>().Take(MaxResultItems))
                    map[pair.Key?.ToString() ?? ""] = Token(ToJson(pair.Value, depth + 1, budget));
                return map;
            }
            if (value is IEnumerable items)
                return new JArray(items.Cast<object>().Take(MaxResultItems).Select(item => Token(ToJson(item, depth + 1, budget))));
            if (IsFrameworkType(type))
                return value.ToString();

            var result = new JObject();
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                result[field.Name] = Token(ToJson(field.GetValue(value), depth + 1, budget));
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0 || !property.CanRead)
                    continue;
                object member;
                try
                {
                    member = property.GetValue(value);
                }
                catch (Exception)
                {
                    continue; // a getter that throws is left out
                }
                result[property.Name] = Token(ToJson(member, depth + 1, budget));
            }
            return result;
        }

        static JToken Token(object converted) => converted as JToken ?? (converted == null ? JValue.CreateNull() : new JValue(converted));

        static bool IsUnityType(Type type)
        {
            var ns = type.Namespace ?? "";
            return ns.StartsWith("UnityEngine", StringComparison.Ordinal) || ns.StartsWith("UnityEditor", StringComparison.Ordinal);
        }

        // Anonymous types have no namespace and stay readable.
        static bool IsFrameworkType(Type type)
        {
            var ns = type.Namespace ?? "";
            return ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal) || ns.StartsWith("Microsoft.", StringComparison.Ordinal);
        }
    }
}
