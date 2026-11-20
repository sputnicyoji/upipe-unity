using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Upipe.Editor
{
    // Loopback HTTP server that speaks the request shapes the official `unity` CLI sends
    // (see the public com.unity.pipeline "Connectivity" documentation).
    [InitializeOnLoad]
    internal static class UpipeServer
    {
        public const string Version = "0.1.0";

        const int FirstPort = 7800;
        const int LastPort = 7849;
        const int HeartbeatMilliseconds = 15000;
        const int MaxBodyBytes = 4 * 1024 * 1024;
        // Each waiting exec holds a thread-pool thread; past this many, new ones are refused.
        const int MaxExecInFlight = 64;
        const int ReloadDrainMilliseconds = 500;
        const string TokenKey = "Upipe.Token";
        const string PortKey = "Upipe.Port";
        const string StartedAtKey = "Upipe.StartedAt";
        static readonly string[] s_Capabilities = { "exec.argv" };
        static readonly string[] s_Details = { "full", "compact", "tags", "groups" };
        static readonly string[] s_Sorts = { "name", "package" };
        static readonly string[] s_Orders = { "asc", "desc" };

        public static readonly string ProjectPath;

        // Absolute, or relative to the project root. A path written on Windows (Scripts\Tool.cs) works on macOS too,
        // where a backslash is never a separator.
        internal static string ResolveProjectPath(string path)
        {
            if (Path.DirectorySeparatorChar == '/')
                path = path.Replace('\\', '/');
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(ProjectPath, path));
        }
        public static readonly string UnityVersion;
        public static readonly string Mode;

        static readonly int s_Pid;
        static readonly string s_Token;
        static readonly string s_StartedAt;
        static readonly string s_DescriptorPath;
        static readonly object s_Lock = new object();
        static volatile HttpListener s_Listener;
        static int s_Port;
        static Timer s_Heartbeat;
        static string s_LastHeartbeat;
        static int s_ExecInFlight;

        static UpipeServer()
        {
            MainThreadQueue.Initialize();
            ProjectPath = Path.GetDirectoryName(Application.dataPath);
            UnityVersion = Application.unityVersion;
            Mode = Application.isBatchMode ? "batchmode" : "editor";
            s_Pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            s_Token = GetOrCreateToken();
            s_StartedAt = SessionState.GetString(StartedAtKey, "");
            if (s_StartedAt.Length == 0)
            {
                s_StartedAt = DateTime.UtcNow.ToString("o");
                SessionState.SetString(StartedAtKey, s_StartedAt);
            }
            s_DescriptorPath = DescriptorFile.PathFor(ProjectPath);

            // Commands register even while the server is off: the menu can start it later, and tests use the registry.
            BuiltInCommands.Register();
            JobManager.Live.Initialize();
            // Before Stop, which fails the queued work: the job table is saved first, unfinished jobs as interrupted.
            AssemblyReloadEvents.beforeAssemblyReload += JobManager.Live.SaveForReload;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
            if (UpipeSettings.IsEnabled())
                Start();
            else
            {
                DescriptorFile.DeleteIfOwned(s_DescriptorPath, s_Pid, orStale: true);
                Debug.Log("[Upipe] Server disabled (Tools/Upipe/Server Enabled or UPIPE_ENABLE); not listening.");
            }
        }

        public static bool IsRunning => s_Listener != null;

        // Starts or stops the server to match the setting, without restarting the Editor.
        public static void ApplySettings()
        {
            var enabled = UpipeSettings.IsEnabled();
            if (enabled && !IsRunning)
                Start();
            else if (!enabled && IsRunning)
            {
                Stop();
                Debug.Log("[Upipe] Server stopped; descriptor removed.");
            }
        }

        public static void Start()
        {
            lock (s_Lock)
            {
                if (s_Listener != null)
                    return;
                if (!StartListener())
                {
                    Debug.LogError($"[Upipe] No free port in {FirstPort}-{LastPort}; server not started.");
                    return;
                }
                Publish();
                s_Heartbeat = new Timer(_ => Publish(), null, HeartbeatMilliseconds, HeartbeatMilliseconds);
            }
            Debug.Log($"[Upipe] HTTP server listening on 127.0.0.1:{s_Port}");
        }

        // Kept in SessionState so the token survives domain reloads within one Editor session.
        static string GetOrCreateToken()
        {
            var token = SessionState.GetString(TokenKey, "");
            if (token.Length > 0)
                return token;
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create())
                random.GetBytes(bytes);
            token = Convert.ToBase64String(bytes);
            SessionState.SetString(TokenKey, token);
            return token;
        }

        static bool StartListener()
        {
            var preferred = SessionState.GetInt(PortKey, 0);
            var candidates = Enumerable.Range(FirstPort, LastPort - FirstPort + 1);
            if (preferred > 0)
                candidates = new[] { preferred }.Concat(candidates.Where(p => p != preferred));

            foreach (var port in candidates)
            {
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                }
                catch (Exception)
                {
                    listener.Close();
                    continue;
                }
                s_Listener = listener;
                s_Port = port;
                SessionState.SetInt(PortKey, port);
                new Thread(() => AcceptLoop(listener)) { IsBackground = true, Name = "Upipe.Accept" }.Start();
                return true;
            }
            return false;
        }

        static void AcceptLoop(HttpListener listener)
        {
            while (true)
            {
                HttpListenerContext context;
                try
                {
                    context = listener.GetContext();
                }
                catch (Exception e)
                {
                    if (s_Listener != listener || !listener.IsListening)
                        return; // stopped on purpose
                    // A transient accept failure must not leave a live descriptor pointing at a dead loop.
                    Debug.LogWarning($"[Upipe] Accept failed, continuing: {e.Message}");
                    Thread.Sleep(100);
                    continue;
                }
                ThreadPool.QueueUserWorkItem(_ => Handle(context));
            }
        }

        static void Publish()
        {
            lock (s_Lock)
            {
                if (s_Listener == null)
                    return;
                s_LastHeartbeat = DateTime.UtcNow.ToString("o");
                DescriptorFile.Write(s_DescriptorPath, new JObject
                {
                    ["pid"] = s_Pid,
                    ["port"] = s_Port,
                    ["projectPath"] = ProjectPath,
                    ["projectName"] = Path.GetFileName(ProjectPath),
                    ["unityVersion"] = UnityVersion,
                    ["mode"] = Mode,
                    ["startedAt"] = s_StartedAt,
                    ["lastHeartbeat"] = s_LastHeartbeat,
                    ["evalToken"] = s_Token,
                    ["capabilities"] = new JArray(s_Capabilities),
                    ["role"] = "main",
                    ["instanceName"] = "Main Editor",
                    ["endpoints"] = new JArray(new JObject { ["type"] = "http", ["port"] = s_Port.ToString() }),
                });
            }
        }

        // Idempotent: also runs on every domain reload and on quit, whether or not the server was started.
        public static void Stop()
        {
            lock (s_Lock)
            {
                s_Heartbeat?.Dispose();
                s_Heartbeat = null;
                var listener = s_Listener;
                s_Listener = null;
                DescriptorFile.DeleteIfOwned(s_DescriptorPath, s_Pid);
                // Queued work dies with the domain: fail it so waiting requests answer 503 retryable,
                // then give them a moment to write that reply before the listener drops connections.
                MainThreadQueue.FailPending();
                var deadline = DateTime.UtcNow.AddMilliseconds(ReloadDrainMilliseconds);
                while (Volatile.Read(ref s_ExecInFlight) > 0 && DateTime.UtcNow < deadline)
                    Thread.Sleep(10);
                try
                {
                    listener?.Close();
                }
                catch (Exception)
                {
                    // already closed
                }
            }
        }

        static void Handle(HttpListenerContext context)
        {
            try
            {
                var (status, body) = Route(context.Request);
                Send(context.Response, status, body);
            }
            // A thread abort means the domain is unloading: let it unwind untouched.
            catch (Exception e) when (!(e is ThreadAbortException))
            {
                // Once the server stops or the domain starts to unload, errors here are its closed listener and
                // disposed streams, and logging from this thread while the domain unloads can crash the Editor.
                if (IsRunning && !MainThreadQueue.DomainReloadInProgress)
                    Debug.LogException(e);
                try
                {
                    Send(context.Response, 500, Failure("Internal Server Error", "Unexpected server error; see the Unity console."));
                }
                catch (Exception)
                {
                    // client went away
                }
            }
        }

        static (int, JObject) Route(HttpListenerRequest request)
        {
            var remote = request.RemoteEndPoint;
            if (remote == null || !IPAddress.IsLoopback(remote.Address))
                return (403, Failure("Forbidden", "Only loopback clients are accepted."));
            // Pinning Host defeats DNS rebinding, which a loopback check alone cannot see.
            if (request.Headers["Host"] != $"127.0.0.1:{s_Port}")
                return (400, Failure("Bad Request", $"Host must be 127.0.0.1:{s_Port}."));
            // Non-browser clients never send Origin; refusing it keeps web pages away from this server.
            if (!string.IsNullOrEmpty(request.Headers["Origin"]))
                return (403, Failure("Forbidden", "Browser-originated requests are refused."));
            if (!IsAuthorized(request.Headers["Authorization"]))
                return (401, Failure("Unauthorized", "Missing or wrong bearer token."));

            var route = request.HttpMethod + " " + request.Url.AbsolutePath;
            switch (route)
            {
                case "GET /api/status":
                    return (200, Status());
                case "GET /api/editor_status":
                    return (200, BuiltInCommands.EditorStatus());
                case "GET /api/commands":
                    return Catalog(request.QueryString);
                case "POST /api/exec":
                    if (!TryReadBody(request, out var body))
                        return (413, Failure("Payload Too Large", $"Request body exceeds {MaxBodyBytes} bytes."));
                    if (Interlocked.Increment(ref s_ExecInFlight) > MaxExecInFlight)
                    {
                        Interlocked.Decrement(ref s_ExecInFlight);
                        return (429, Failure("Too Many Requests", $"More than {MaxExecInFlight} commands are already waiting."));
                    }
                    try
                    {
                        return Exec(body);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref s_ExecInFlight);
                    }
                case "GET /api/job":
                    return JobStatus(request.QueryString["id"]);
#if UPIPE_TEST_FRAMEWORK
                case "GET /api/test-status":
                    var detail = TestCommands.NormalizeDetail(request.QueryString["detail"]);
                    return detail != null ? (200, TestCommands.Status(detail)) : BadValue("detail", TestCommands.Details);
#endif
                case "POST /api/job/cancel":
                    if (!TryReadBody(request, out var cancelBody))
                        return (413, Failure("Payload Too Large", $"Request body exceeds {MaxBodyBytes} bytes."));
                    return CancelJob(cancelBody);
                default:
                    return (404, Failure("Not Found", $"No route for {route}."));
            }
        }

        static bool IsAuthorized(string header)
        {
            const string scheme = "Bearer ";
            if (header == null || !header.StartsWith(scheme, StringComparison.Ordinal))
                return false;
            var given = Encoding.UTF8.GetBytes(header.Substring(scheme.Length));
            var expected = Encoding.UTF8.GetBytes(s_Token);
            if (given.Length != expected.Length)
                return false;
            var difference = 0;
            for (var i = 0; i < given.Length; i++)
                difference |= given[i] ^ expected[i];
            return difference == 0;
        }

        static bool TryReadBody(HttpListenerRequest request, out string body)
        {
            body = null;
            if (request.ContentLength64 > MaxBodyBytes)
                return false;
            using (var memory = new MemoryStream())
            {
                var chunk = new byte[8192];
                int read;
                while ((read = request.InputStream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (memory.Length + read > MaxBodyBytes)
                        return false;
                    memory.Write(chunk, 0, read);
                }
                body = Encoding.UTF8.GetString(memory.ToArray());
                return true;
            }
        }

        static JObject Status()
        {
            var status = MainThreadQueue.StateFields();
            status["lastHeartbeat"] = s_LastHeartbeat;
            status["capabilities"] = new JArray(s_Capabilities);
            return status;
        }

        static (int, JObject) Catalog(NameValueCollection query)
        {
            var detail = query["detail"] ?? "full";
            var sort = query["sort"] ?? "name";
            var order = query["order"] ?? "asc";
            var groupBy = query["group_by"] ?? "flat";
            if (!s_Details.Contains(detail))
                return BadValue("detail", s_Details);
            if (!s_Sorts.Contains(sort))
                return BadValue("sort", s_Sorts);
            if (!s_Orders.Contains(order))
                return BadValue("order", s_Orders);
            var isIndex = detail == "tags" || detail == "groups";
            // shortcut: grouped trees (group_by=package|tag|group) are not implemented; add them when a client needs them.
            if (groupBy != "flat")
                return InvalidRequest(isIndex
                    ? "group_by cannot be combined with detail=tags or detail=groups."
                    : "group_by: this server supports only 'flat'.");
            if (sort == "package" && isIndex)
                return InvalidRequest("sort=package cannot be combined with detail=tags or detail=groups.");
            if (!TryParseCount(query["offset"], out var offset))
                return InvalidRequest("offset must be a non-negative integer.");
            if (!TryParseCount(query["limit"], out var limit))
                return InvalidRequest("limit must be a non-negative integer.");

            IEnumerable<CommandEntry> matched = CommandRegistry.Commands;
            var text = query["query"];
            if (!string.IsNullOrEmpty(text))
                matched = matched.Where(c => Contains(c.FullName, text) || Contains(c.CatalogName, text) || Contains(c.Description, text) || c.Tags.Any(t => Contains(t, text)));
            var group = query["group"];
            if (!string.IsNullOrEmpty(group))
                matched = matched.Where(c => CommandRegistry.InGroup(c, group));
            var tag = query["tag"];
            if (!string.IsNullOrEmpty(tag))
                matched = matched.Where(c => c.Tags.Any(t => t == tag || t.StartsWith(tag + "/", StringComparison.Ordinal)));
            // Every command comes from this one package, so sort=package reduces to the full-name order.
            var commands = matched.OrderBy(c => c.FullName, StringComparer.Ordinal).ToList();

            string key;
            List<JObject> rows;
            if (detail == "tags")
            {
                key = "tags";
                rows = PrefixIndex(commands, c => c.Tags, '/', "tag");
            }
            else if (detail == "groups")
            {
                key = "groups";
                rows = PrefixIndex(commands, c => new[] { c.Group }, '.', "group");
            }
            else
            {
                key = "commands";
                rows = commands.Select(c =>
                {
                    var entry = c.ToCatalogEntry();
                    if (detail == "compact")
                    {
                        entry.Remove("parameters");
                        entry.Remove("schema");
                    }
                    return entry;
                }).ToList();
            }
            if (order == "desc")
                rows.Reverse();

            var page = rows.Skip(offset ?? 0).Take(limit ?? int.MaxValue).ToList();
            return (200, new JObject
            {
                [key] = new JArray(page),
                ["count"] = page.Count,
                ["total"] = rows.Count,
                ["offset"] = offset ?? 0,
                ["limit"] = limit.HasValue ? new JValue(limit.Value) : JValue.CreateNull(),
                ["server"] = new JObject { ["version"] = Version, ["port"] = s_Port, ["startTime"] = s_StartedAt },
            });
        }

        // One row per tag or group and per each of its ancestors; count covers the whole subtree.
        static List<JObject> PrefixIndex(IEnumerable<CommandEntry> commands, Func<CommandEntry, IEnumerable<string>> keys, char separator, string field)
        {
            var members = new SortedDictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var command in commands)
            {
                foreach (var value in keys(command))
                {
                    var parts = value.Split(separator);
                    for (var depth = 1; depth <= parts.Length; depth++)
                    {
                        var prefix = string.Join(separator.ToString(), parts.Take(depth));
                        if (!members.TryGetValue(prefix, out var set))
                            members[prefix] = set = new HashSet<string>();
                        set.Add(command.FullName);
                    }
                }
            }
            return members.Select(m => new JObject { [field] = m.Key, ["count"] = m.Value.Count }).ToList();
        }

        static bool TryParseCount(string text, out int? value)
        {
            value = null;
            if (text == null)
                return true;
            if (!int.TryParse(text, out var parsed) || parsed < 0)
                return false;
            value = parsed;
            return true;
        }

        static bool Contains(string haystack, string needle) => haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        static (int, JObject) BadValue(string name, string[] accepted) =>
            InvalidRequest($"Invalid {name}; accepted values: {string.Join(", ", accepted)}.");

        static (int, JObject) Exec(string body)
        {
            JObject request;
            try
            {
                request = JObject.Parse(body);
            }
            catch (JsonException e)
            {
                return (400, Failure("Invalid JSON", e.Message));
            }

            var argv = request["argv"];
            var command = request["command"];
            var forms = (argv != null ? 1 : 0) + (command != null ? 1 : 0) + (request["commandLine"] != null ? 1 : 0);
            if (forms != 1)
                return InvalidRequest("Send exactly one of argv, command or commandLine.");
            if (request["commandLine"] != null)
                return InvalidRequest("commandLine is not supported by this server; send argv.");
            if (argv != null && request["parameters"] != null)
                return InvalidRequest("parameters cannot be sent together with argv.");
            var job = request["job"]?.Type == JTokenType.Boolean && (bool)request["job"];

            int? timeout = null;
            var timeoutToken = request["timeout"];
            if (timeoutToken != null && timeoutToken.Type != JTokenType.Null)
            {
                // ToString keeps integers too large for long (BigInteger) from throwing.
                if (timeoutToken.Type != JTokenType.Integer || !long.TryParse(timeoutToken.ToString(), out var milliseconds))
                    return InvalidRequest("timeout must be an integer number of milliseconds.");
                if (milliseconds > 0)
                    timeout = (int)Math.Min(int.MaxValue, milliseconds);
            }
            var verbose = request["verbose"]?.Type == JTokenType.Boolean && (bool)request["verbose"];

            if (argv != null)
            {
                if (!(argv is JArray tokens) || tokens.Count == 0 || tokens.Any(t => t.Type != JTokenType.String))
                    return InvalidRequest("argv must be a non-empty array of strings.");
                var words = tokens.Select(t => (string)t).ToArray();
                var resolved = CommandRegistry.ResolveTokens(words);
                if (resolved.ListedGroup != null)
                {
                    if (job)
                        return InvalidRequest($"'{resolved.ListedGroup}' is a command group; name one of its commands to run it as a job.");
                    return (200, new JObject
                    {
                        ["success"] = true,
                        ["command"] = resolved.ListedGroup,
                        ["result"] = CommandRegistry.ListGroup(resolved.ListedGroup),
                        ["listedGroup"] = resolved.ListedGroup,
                        ["parameters"] = new JObject(),
                    });
                }
                if (resolved.Command == null)
                    return (400, NotFound(resolved.Typed, resolved.NotFoundDetails, echoCommand: true));
                var bound = CommandRegistry.BindArgv(resolved.Command, resolved.RemainingTokens);
                return job
                    ? Detach(resolved.Command, bound, echoCommand: true)
                    : Run(resolved.Command, bound, timeout, echoCommand: true, echoParameters: true);
            }

            if (command.Type != JTokenType.String)
                return InvalidRequest("command must be a string.");
            var parameters = request["parameters"];
            if (parameters != null && parameters.Type != JTokenType.Object && parameters.Type != JTokenType.Null)
                return InvalidRequest("parameters must be an object.");
            var entry = CommandRegistry.ResolveStructured((string)command, out var details);
            if (entry == null)
                return (400, NotFound((string)command, details, echoCommand: verbose));
            var boundStructured = CommandRegistry.BindStructured(entry, parameters as JObject);
            return job
                ? Detach(entry, boundStructured, echoCommand: verbose)
                : Run(entry, boundStructured, timeout, echoCommand: verbose, echoParameters: false);
        }

        // Bad arguments and a settling Editor are answered at once, for jobs too (the CLI prints them on --detach).
        static (int, JObject)? Refusal(CommandEntry command, BindResult bound, bool echoCommand)
        {
            if (!bound.Ok)
            {
                var reply = Failure("Invalid Command Arguments",
                    $"{command.FullName}: " + string.Join("; ", bound.Problems.Select(p => $"{p["kind"]} {p["name"] ?? p["token"]}")));
                reply["errorCode"] = "INVALID_COMMAND_ARGS";
                reply["argProblems"] = bound.Problems;
                reply["commandSchema"] = command.ToCatalogEntry();
                return (400, WithCommand(reply, command, echoCommand));
            }
            if ((command.MainThreadRequired || command.WaitsForSettle) && !MainThreadQueue.Settled)
                return (503, WithCommand(Busy("settling",
                    "The Editor is still importing or compiling after startup. Retry once /api/status reports ready."), command, echoCommand));
            return null;
        }

        // Main-thread commands go through the queue; the others run on the thread pool, or on a thread of their own
        // for a job, which may run for a long time. `started` runs right before the handler.
        static Task<object> StartCommand(CommandEntry command, JObject values, CancellationToken token, Action started, bool ownThread)
        {
            if (command.MainThreadRequired)
                return MainThreadQueue.Run(() =>
                {
                    started?.Invoke();
                    return command.Handler(values);
                }, token);
            Func<object> work = () =>
            {
                started?.Invoke();
                return command.CancellableHandler != null ? command.CancellableHandler(values, token) : command.Handler(values);
            };
            if (!ownThread)
                return Task.Run(work, token);
            var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            new Thread(() =>
            {
                try
                {
                    completion.TrySetResult(work());
                }
                catch (Exception e)
                {
                    completion.TrySetException(e);
                }
            }) { IsBackground = true, Name = "Upipe.Job" }.Start();
            return completion.Task;
        }

        // `"job": true`: answers with the job id at once. The CLI sends timeout 2147483647 with --detach, so only the
        // command's own timeout-ms / timeout parameters bound a job; without them it runs until it ends or is cancelled.
        static (int, JObject) Detach(CommandEntry command, BindResult bound, bool echoCommand)
        {
            var refusal = Refusal(command, bound, echoCommand);
            if (refusal != null)
                return refusal.Value;
            var timeout = TimeoutMilliseconds(null, bound.Values, 0);
            JObject job;
            try
            {
                job = JobManager.Live.Submit(command.FullName, timeout > 0 ? timeout : (int?)null,
                    (token, started) => StartCommand(command, bound.Values, token, started, ownThread: true));
            }
            catch (EditorReloadingException e)
            {
                return (503, WithCommand(Busy("domain_reload", e.Message), command, echoCommand));
            }
            if (job == null)
                return (429, WithCommand(Failure("Too Many Requests",
                    $"{JobManager.MaxUnfinishedJobs} jobs are queued or running; wait for one to finish or cancel one."), command, echoCommand));
            var reply = new JObject { ["success"] = true };
            WithCommand(reply, command, echoCommand);
            reply["result"] = new JObject { ["jobId"] = job["jobId"], ["state"] = job["state"] };
            return (200, reply);
        }

        static (int, JObject) JobStatus(string id)
        {
            if (string.IsNullOrEmpty(id))
                return InvalidRequest("Give the job id as ?id=<jobId>.");
            var job = JobManager.Live.Status(id);
            return job != null ? (200, job) : JobNotFound(id);
        }

        static (int, JObject) CancelJob(string body)
        {
            JToken id;
            try
            {
                id = JObject.Parse(body)["id"];
            }
            catch (JsonException e)
            {
                return (400, Failure("Invalid JSON", e.Message));
            }
            if (id == null || id.Type != JTokenType.String || ((string)id).Length == 0)
                return InvalidRequest("Send {\"id\": \"<jobId>\"}.");
            var job = JobManager.Live.Cancel((string)id);
            return job != null ? (200, job) : JobNotFound((string)id);
        }

        static (int, JObject) JobNotFound(string id) => (404, Failure("Job Not Found",
            $"No job with id {id}. The last 100 finished jobs are kept for 1 hour."));

        static (int, JObject) Run(CommandEntry command, BindResult bound, int? requestTimeout, bool echoCommand, bool echoParameters)
        {
            JObject reply;
            var timeout = TimeoutMilliseconds(requestTimeout, bound.Values, command.DefaultTimeoutSeconds);
            var refusal = Refusal(command, bound, echoCommand);
            if (refusal != null)
                return refusal.Value;

            using (var timedOut = new CancellationTokenSource())
            {
                var task = StartCommand(command, bound.Values, timedOut.Token, null, ownThread: false);
                bool finished;
                try
                {
                    finished = task.Wait(timeout);
                }
                catch (AggregateException e)
                {
                    var inner = Unwrap(e);
                    if (inner is EditorReloadingException)
                        return (503, WithCommand(Busy("domain_reload", inner.Message), command, echoCommand));
                    reply = inner is CommandFailure failure
                        ? Failure(failure.Error, failure.Message)
                        : Failure("Command Execution Failed", inner.Message);
                    if (inner is CommandFailure withResult && withResult.Result != null)
                        reply["result"] = withResult.Result;
                    if (echoParameters)
                        reply["parameters"] = bound.Values;
                    return (400, WithCommand(reply, command, echoCommand));
                }
                if (!finished)
                {
                    // Still queued: cancelling guarantees it never runs. Already running: it cannot be stopped.
                    timedOut.Cancel();
                    reply = Failure("Request Timed Out", $"No result within {timeout} ms. If the command had already started, it may still finish.");
                    reply["errorCode"] = "TIMEOUT";
                    return (504, WithCommand(reply, command, echoCommand));
                }

                reply = new JObject { ["success"] = true };
                WithCommand(reply, command, echoCommand);
                reply["result"] = task.Result == null ? JValue.CreateNull() : JToken.FromObject(task.Result);
                if (echoParameters)
                    reply["parameters"] = bound.Values;
                return (200, reply);
            }
        }

        // Order from the docs: the request's timeout (ms), then the command's own `timeout` parameter (s), then its
        // default. A `timeout-ms` parameter comes before `timeout`: the unity CLI's skill docs give eval timeouts in ms.
        // The CLI always sends a request timeout (30000 by default, 60000 from `unity mcp`), so the parameters only
        // decide for clients that leave it out.
        internal static int TimeoutMilliseconds(int? requestTimeout, JObject parameters, int defaultSeconds)
        {
            if (requestTimeout.HasValue)
                return requestTimeout.Value;
            if (long.TryParse(parameters["timeout-ms"]?.ToString(), out var milliseconds) && milliseconds > 0)
                return (int)Math.Min(milliseconds, int.MaxValue);
            if (long.TryParse(parameters["timeout"]?.ToString(), out var seconds) && seconds > 0)
                return (int)(Math.Min(seconds, int.MaxValue / 1000L) * 1000); // saturate instead of overflowing
            return defaultSeconds * 1000;
        }

        // A handler that waits on other tasks can surface AggregateExceptions nested several deep.
        internal static Exception Unwrap(Exception e)
        {
            while (e is AggregateException aggregate && aggregate.InnerException != null)
                e = aggregate.InnerException;
            return e;
        }

        static JObject WithCommand(JObject reply, CommandEntry command, bool echoCommand)
        {
            if (echoCommand)
                reply["command"] = command.FullName;
            return reply;
        }

        static JObject Busy(string reason, string details)
        {
            var reply = Failure("Server Busy", details);
            reply["status"] = "busy";
            reply["busyReason"] = reason;
            reply["retryable"] = true;
            return reply;
        }

        static (int, JObject) InvalidRequest(string details) => (400, Failure("Invalid Request", details));

        static JObject NotFound(string typed, string details, bool echoCommand)
        {
            var reply = Failure("Command Not Found", details);
            if (echoCommand)
                reply["command"] = typed;
            return reply;
        }

        static JObject Failure(string error, string details)
        {
            return new JObject { ["success"] = false, ["error"] = error, ["errorDetails"] = details };
        }

        static void Send(HttpListenerResponse response, int status, JObject body)
        {
            var bytes = Encoding.UTF8.GetBytes(body.ToString(Formatting.None));
            response.StatusCode = status;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.OutputStream.Close();
        }
    }
}
