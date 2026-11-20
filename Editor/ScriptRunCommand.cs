using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Upipe.Editor
{
    // `script run`: compiles one .cs file from anywhere on disk with the Editor's Roslyn (CscCompiler), loads it from
    // memory and calls a static method on the main thread. Nothing is imported and the domain does not reload. A
    // returned awaitable (Task, UniTask, ...) is awaited through reflection, so no awaitable library is referenced.
    internal static class ScriptRunCommand
    {
        const BindingFlags StaticMethods = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;
        static readonly string[] s_DefaultEntries = { "Run", "Main" };

        static string[] s_EditorDefines = Array.Empty<string>();

        // Called on the main thread while the server starts, after ScriptEvalCommand set up CscCompiler.
        public static void Register()
        {
            // Changing the defines recompiles and reloads the domain, which registers again.
            s_EditorDefines = EditorUserBuildSettings.activeScriptCompilationDefines;

            CommandRegistry.Register(new CommandEntry
            {
                Group = "script",
                Name = "run",
                Alias = "script_run",
                Description = "Compile a .cs file from anywhere on disk (no asset import, no domain reload) and call one of its " +
                    "static methods. A returned Task, Task<T>, UniTask or other awaitable is awaited and its value is the result.",
                Tags = new[] { "scripts" },
                Parameters = new[]
                {
                    new CommandParameter { Name = "file", Required = true, Description = "Path to the .cs file, absolute or relative to the project root." },
                    new CommandParameter { Name = "entry", Description = "Static method to call: Type.Method or Namespace.Type.Method. Default: the file's only static Run or Main." },
                    new CommandParameter { Name = "args", Description = "Arguments as JSON: the value itself for a one-parameter method, else an array (by position) or object (by name). A CancellationToken parameter is passed for you." },
                    new CommandParameter { Name = "defines", Description = "Extra preprocessor symbols separated by ; or , (the Editor's script defines always apply)." },
                    new CommandParameter { Name = "timeout-ms", IsInteger = true, Description = "Timeout in milliseconds; it also cancels the method's CancellationToken." },
                },
                MainThreadRequired = false,
                WaitsForSettle = true,
                DefaultTimeoutSeconds = 30,
                CancellableHandler = Run,
            });
        }

        // `cancel` fires on the request timeout or a job cancel: this thread stops waiting and the method's
        // CancellationToken is cancelled; a method that ignores it keeps running on the main thread.
        static object Run(JObject parameters, CancellationToken cancel)
        {
            var path = ScriptEvalCommand.ResolveScriptPath((string)parameters["file"]);
            var entry = (string)parameters["entry"];
            var args = ParseArgs(parameters["args"]);
            var defines = s_EditorDefines.Concat(((string)parameters["defines"] ?? "").Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries));

            var name = "UpipeRun_" + Guid.NewGuid().ToString("N");
            // 0436: a file that is also part of the project redefines the project's own types, which is fine here.
            var (assembly, symbols, errors) = CscCompiler.Compile(name, File.ReadAllText(path), "0436", defines);
            if (errors.Count > 0)
                throw new CommandFailure("Command Execution Failed", $"COMPILATION_ERROR: Compilation Failed: {errors.Count} error(s)\n" +
                    string.Join("\n", errors.Select(e => $"line {e.Line}, col {e.Column - 1}: error {e.Id}: {e.Message}")));
            cancel.ThrowIfCancellationRequested();

            var running = OnMainThread(() => Start(assembly, symbols, entry, args, cancel), cancel, "could not start the script");
            try
            {
                // Blocks this thread only; the method's awaits resume where their awaitable resumes them.
                ((Task<object>)running).Wait(cancel);
            }
            catch (AggregateException e)
            {
                var error = UpipeServer.Unwrap(e);
                // Frames in the script read as the user's file rather than the temporary copy.
                var trace = (error.StackTrace ?? "").Replace(Path.Combine(CscCompiler.WorkDirectory, name + ".cs"), path);
                throw new CommandFailure("Command Execution Failed",
                    $"RUNTIME_EXCEPTION: Runtime Error. {error.GetType().Name}: {error.Message}\n{trace}");
            }
            var value = ((Task<object>)running).Result;
            try
            {
                // Reading a Unity object (even its name) is main-thread only.
                return OnMainThread(() => ScriptEvalCommand.ToJson(value), cancel, "could not convert the result");
            }
            catch (EditorReloadingException)
            {
                // Not the retryable 503: the method has run, and a client that resends would run it again.
                throw new CommandFailure("Command Execution Failed",
                    "The script ran, but a domain reload started before its result could be read.");
            }
        }

        internal static object OnMainThread(Func<object> work, CancellationToken cancel, string what)
        {
            try
            {
                return MainThreadQueue.Run(work, cancel).Result;
            }
            catch (AggregateException e)
            {
                var error = UpipeServer.Unwrap(e);
                if (error is EditorReloadingException || error is OperationCanceledException || error is CommandFailure)
                    throw error;
                throw new CommandFailure("Command Execution Failed", $"INTERNAL_ERROR: {what}: {error.Message}");
            }
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

        // Main thread. A synchronous throw from the method fails the returned task like an asynchronous one.
        static Task<object> Start(byte[] assembly, byte[] symbols, string entry, JToken args, CancellationToken cancel)
        {
            var loaded = symbols != null ? Assembly.Load(assembly, symbols) : Assembly.Load(assembly);
            var method = FindEntry(loaded, entry);
            var arguments = BindArguments(method, args, cancel);
            object value;
            try
            {
                value = method.Invoke(null, arguments);
            }
            catch (TargetInvocationException e)
            {
                return Task.FromException<object>(e.InnerException);
            }
            return Await(value, method.ReturnType);
        }

        static MethodInfo FindEntry(Assembly assembly, string entry)
        {
            IEnumerable<MethodInfo> candidates;
            if (string.IsNullOrEmpty(entry))
                candidates = assembly.GetTypes().SelectMany(t => t.GetMethods(StaticMethods)).Where(m => s_DefaultEntries.Contains(m.Name));
            else
            {
                var dot = entry.LastIndexOf('.');
                if (dot <= 0 || dot == entry.Length - 1)
                    throw CommandFailure.InvalidUsage($"--entry must be Type.Method or Namespace.Type.Method: {entry}");
                var typeName = entry.Substring(0, dot);
                var methodName = entry.Substring(dot + 1);
                candidates = assembly.GetTypes()
                    .Where(t => t.FullName == typeName || t.FullName.Replace('+', '.') == typeName || t.Name == typeName)
                    .SelectMany(t => t.GetMethods(StaticMethods))
                    .Where(m => m.Name == methodName);
            }
            var matches = candidates.Where(m => !m.ContainsGenericParameters).ToList();
            if (matches.Count == 1)
                return matches[0];
            var wanted = string.IsNullOrEmpty(entry) ? "static Run or Main method" : "static method " + entry;
            if (matches.Count == 0)
                throw CommandFailure.InvalidUsage($"The file has no {wanted}; name one with --entry Type.Method.");
            throw CommandFailure.InvalidUsage($"More than one {wanted} in the file ({string.Join(", ", matches.Select(Describe))}); " +
                "name one with --entry Type.Method.");
        }

        static string Describe(MethodInfo method) =>
            $"{method.DeclaringType.FullName}.{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})";

        // One bindable parameter takes the whole JSON value; more take an array by position or an object by name.
        // CancellationToken parameters get the run's token and are never bound.
        internal static object[] BindArguments(MethodInfo method, JToken args, CancellationToken cancel)
        {
            var parameters = method.GetParameters();
            var values = new object[parameters.Length];
            var bindable = parameters.Where(p => p.ParameterType != typeof(CancellationToken)).ToList();
            foreach (var p in parameters.Where(p => p.ParameterType == typeof(CancellationToken)))
                values[p.Position] = cancel;

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
                    if (e.InnerException is OperationCanceledException)
                        completion.TrySetCanceled();
                    else
                        completion.TrySetException(e.InnerException);
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
