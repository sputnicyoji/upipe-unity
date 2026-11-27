// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Yoji

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.MPE;
using UnityEngine;

namespace Upipe.Editor
{
    // Loopback HTTP server that speaks the request shapes the official `unity` CLI sends
    // (see the public com.unity.pipeline "Connectivity" documentation).
    [InitializeOnLoad]
    internal static class UpipeServer
    {
        internal const string FallbackVersion = "0.3.5";
        public static string Version { get; private set; } = FallbackVersion;

        const int FirstPort = 7800;
        const int LastPort = 7849;
        const int HeartbeatMilliseconds = 15000;
        const int MaxBodyBytes = 4 * 1024 * 1024;
        // Each waiting exec holds a thread-pool thread; past this many, new ones are refused.
        const int MaxExecInFlight = 64;
        const int ReloadDrainMilliseconds = 500;
        // On Windows the old domain's listening socket stays bound for up to ~200 ms after a reload (measured), and a
        // moved port strands a CLI polling the old one (`unity recompile` waits out its timeout), so wait for it.
        const int PreferredPortWaitMilliseconds = 2000;
        const string TokenKey = "Upipe.Token";
        const string PortKey = "Upipe.Port";
        const string StartedAtKey = "Upipe.StartedAt";
#if UPIPE_TEST_FRAMEWORK
        static readonly string[] s_Capabilities = { "exec.argv", "tests.camelCase" };
#else
        static readonly string[] s_Capabilities = { "exec.argv" };
#endif
        static readonly string[] s_Details = { "full", "compact", "tags", "groups" };
        static readonly string[] s_Sorts = { "name", "package" };
        static readonly string[] s_Orders = { "asc", "desc" };

        public static readonly string ProjectPath;

        const uint HandleFlagInherit = 1;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint bufferLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

        // On Windows, temp paths can arrive in 8.3 short form while Roslyn expands `#line` paths to long names in
        // diagnostics and Portable PDBs. Expanding existing paths keeps diagnostics and --extra-files matching aligned.
        internal static string CanonicalizePath(string fullPath)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || string.IsNullOrEmpty(fullPath))
                return fullPath;
            var buffer = new StringBuilder(260);
            var length = GetLongPathNameW(fullPath, buffer, (uint)buffer.Capacity);
            if (length >= buffer.Capacity)
            {
                buffer = new StringBuilder((int)length);
                length = GetLongPathNameW(fullPath, buffer, (uint)buffer.Capacity);
            }
            return length > 0 && length < buffer.Capacity ? buffer.ToString() : fullPath;
        }

        // Absolute, or relative to the project root. A path written on Windows (Scripts\Tool.cs) works on macOS too,
        // where a backslash is never a separator.
        internal static string ResolveProjectPath(string path)
        {
            if (Path.DirectorySeparatorChar == '/')
                path = path.Replace('\\', '/');
            return CanonicalizePath(Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(ProjectPath, path)));
        }
        // How this platform's file systems compare paths: Windows and macOS ignore case by default, Linux does not.
        internal static readonly StringComparer PathComparer =
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        internal static bool PathsIgnoreCase => PathComparer == StringComparer.OrdinalIgnoreCase;

        public static readonly string UnityVersion;
        public static readonly string Mode;

        static readonly int s_Pid;
        static string s_Token;
        static string s_StartedAt;
        static readonly string s_DescriptorPath;
        static readonly object s_Lock = new object();
        static readonly object s_DescriptorLock = new object();
        static readonly Type s_MonoEndPointManager = typeof(HttpListener).Assembly.GetType("System.Net.EndPointManager");
        static volatile HttpListener s_Listener;
        static volatile int s_Port;
        static object s_LocalhostPrefix;
        static Timer s_Heartbeat;
        static volatile string s_LastHeartbeat;
        static int s_ExecInFlight;
        static int s_RequestsInFlight;
        // HTTP requests being answered right now, so `editor quit` can wait for its own reply to go out.
        internal static int RequestsInFlight => Volatile.Read(ref s_RequestsInFlight);
        static int s_LiveJobThreads;
        static CancellationTokenSource s_Shutdown;
        static bool s_LifecycleHooked;
        static readonly bool s_IsSecondaryProcess;
        internal static bool IsSecondaryProcess => s_IsSecondaryProcess;

        // Asset import workers and MPE secondary processes load Editor assemblies and run [InitializeOnLoad];
        // they must never bind ports, attach runtime hooks, or touch the project's descriptor file.
        // Checking ProcessLevel.Secondary (instead of != Main) avoids skipping batchmode if level is Undefined.
        internal static bool ShouldSkipProcess(bool isAssetImportWorker, ProcessLevel processLevel) =>
            isAssetImportWorker || processLevel == ProcessLevel.Secondary;

        static UpipeServer()
        {
            s_IsSecondaryProcess = ShouldSkipProcess(AssetDatabase.IsAssetImportWorkerProcess(), ProcessService.level);
            if (s_IsSecondaryProcess)
                return;

            var packageVersion = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UpipeServer).Assembly)?.version;
            if (!string.IsNullOrEmpty(packageVersion))
                Version = packageVersion;

            ProjectPath = CanonicalizePath(Path.GetDirectoryName(Application.dataPath));
            UnityVersion = Application.unityVersion;
            Mode = Application.isBatchMode ? "batchmode" : "editor";
            s_Pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            s_DescriptorPath = DescriptorFile.PathFor(ProjectPath);

            // Commands register in memory even while the server is off: the menu can start it later, and unit tests read the registry.
            BuiltInCommands.Register();
            if (UpipeSettings.IsEnabled())
                Start();
            else
            {
                DescriptorFile.DeleteIfOwned(s_DescriptorPath, s_Pid, orStale: true);
                Debug.Log("[Upipe] Server disabled (Tools/Upipe/Server Enabled or UPIPE_ENABLE); not listening.");
            }
        }

        public static bool IsRunning => s_Listener != null;

        static void EnsureSessionMetadata()
        {
            if (string.IsNullOrEmpty(s_Token))
                s_Token = GetOrCreateToken();
            if (string.IsNullOrEmpty(s_StartedAt))
            {
                s_StartedAt = SessionState.GetString(StartedAtKey, "");
                if (s_StartedAt.Length == 0)
                {
                    s_StartedAt = DateTime.UtcNow.ToString("o");
                    SessionState.SetString(StartedAtKey, s_StartedAt);
                }
            }
        }

        static void HookLifecycle()
        {
            if (s_LifecycleHooked)
                return;
            s_LifecycleHooked = true;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnEditorQuitting;
        }

        static void UnhookLifecycle()
        {
            if (!s_LifecycleHooked)
                return;
            s_LifecycleHooked = false;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            EditorApplication.quitting -= OnEditorQuitting;
        }

        static void AttachModules()
        {
            JobManager.Live.Resume();
            MainThreadQueue.Attach();
            ConsoleCommands.Attach();
            RecompileCommands.Attach();
            AutoTick.Attach();
            PlayModeCommands.Attach();
#if UPIPE_TEST_FRAMEWORK
            TestCommands.Attach();
#endif
            EditorQuitCommand.Attach();
        }

        static void DetachModules(bool forReload, bool serverStopped)
        {
            CscCompiler.Detach();
            ScriptEvalCommand.Detach();
            MainThreadQueue.Detach(forReload);
            ConsoleCommands.Detach();
            RecompileCommands.Detach();
            AutoTick.Detach();
            PlayModeCommands.Detach();
#if UPIPE_TEST_FRAMEWORK
            TestCommands.Detach(serverStopped);
#endif
        }

        static void OnBeforeAssemblyReload()
        {
            MainThreadQueue.MarkDomainReloading();
            JobManager.Live.SaveForReload();
            ConsoleCommands.Persist();
#if UPIPE_TEST_FRAMEWORK
            TestCommands.SaveForReload();
#endif
            Stop(forReload: true);
        }

        static void OnEditorQuitting() => Stop(forReload: false);

        // Starts or stops the server to match the setting, without restarting the Editor.
        public static void ApplySettings()
        {
            if (s_IsSecondaryProcess)
                return;
            var enabled = UpipeSettings.IsEnabled();
            if (enabled && !IsRunning)
                Start();
            else if (!enabled && IsRunning)
            {
                Stop(forReload: false);
                Debug.Log("[Upipe] Server stopped; descriptor removed.");
            }
        }

        public static void Start()
        {
            if (s_IsSecondaryProcess)
                return;
            int port;
            lock (s_Lock)
            {
                if (s_Listener != null)
                    return;
                EnsureSessionMetadata();
                s_Shutdown = new CancellationTokenSource();
                HookLifecycle();
                AttachModules();
                if (!StartListener())
                {
                    var failedShutdown = s_Shutdown;
                    s_Shutdown = null;
                    UnhookLifecycle();
                    // Never started: a restored test run is not ended, and the saved copies stay for the next domain.
                    DetachModules(forReload: false, serverStopped: false);
                    CancelQuietly(failedShutdown);
                    Debug.LogError($"[Upipe] No free port in {FirstPort}-{LastPort}; server not started.");
                    return;
                }
                port = s_Port;
                s_Heartbeat = new Timer(_ => Publish(), null, HeartbeatMilliseconds, HeartbeatMilliseconds);
            }
            Publish();
            Debug.Log($"[Upipe] HTTP server listening on 127.0.0.1:{port}");
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
                var waitUntil = port == preferred ? DateTime.UtcNow.AddMilliseconds(PreferredPortWaitMilliseconds) : DateTime.MinValue;
                HttpListener listener;
                object localhostPrefix = null;
                while ((listener = TryListen(port, out localhostPrefix)) == null && DateTime.UtcNow < waitUntil)
                    Thread.Sleep(20);
                if (listener == null)
                    continue;
                s_Listener = listener;
                s_Port = port;
                s_LocalhostPrefix = localhostPrefix;
                SessionState.SetInt(PortKey, port);
                new Thread(() => AcceptLoop(listener)) { IsBackground = true, Name = "Upipe.Accept" }.Start();
                return true;
            }
            return false;
        }

        // A started listener on the port, or null if it is taken.
        static HttpListener TryListen(int port, out object localhostPrefix)
        {
            localhostPrefix = null;
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            if (s_MonoEndPointManager == null)
                listener.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                listener.Start();
                localhostPrefix = ConfigureMonoLoopbackListener(listener, port);
                return listener;
            }
            catch (Exception)
            {
                RemoveMonoLoopbackPrefix(listener, port, localhostPrefix);
                localhostPrefix = null;
                try
                {
                    listener.Abort();
                }
                catch (Exception)
                {
                    // already closed
                }
                return null;
            }
        }

        static object GetMonoLoopbackEndPointListener(int port)
        {
            var map = s_MonoEndPointManager?.GetField("ip_to_endpoints", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as Hashtable;
            if (map == null)
                return null;
            lock (map)
            {
                var ports = map[IPAddress.Loopback] as Hashtable;
                return ports?[port];
            }
        }

        static object ConfigureMonoLoopbackListener(HttpListener listener, int port)
        {
            if (s_MonoEndPointManager == null)
                return null;
            var endpointListener = GetMonoLoopbackEndPointListener(port);
            if (endpointListener == null)
                return null;
            try
            {
                // Prevent child processes spawned with UseShellExecute=false from inheriting the listening socket handle
                // on Windows (otherwise a child process keeps the port bound across domain reloads).
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var sock = endpointListener.GetType().GetField("sock", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(endpointListener) as Socket;
                    if (sock != null)
                        SetHandleInformation(sock.Handle, HandleFlagInherit, 0);
                }
                // Mono binds http://localhost:<port>/ only to ::1 (Dns.GetHostAddresses("localhost")[0]), leaving 127.0.0.1
                // without a localhost prefix. Registering localhost directly on the IPv4 EndPointListener accepts
                // `Host: localhost:<port>` when the unity CLI falls back to localhost after ECONNREFUSED on reload.
                var prefixType = typeof(HttpListener).Assembly.GetType("System.Net.ListenerPrefix");
                if (prefixType == null)
                    return null;
                var prefix = Activator.CreateInstance(prefixType, new object[] { $"http://localhost:{port}/" });
                var addPrefix = endpointListener.GetType().GetMethod("AddPrefix", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                addPrefix?.Invoke(endpointListener, new object[] { prefix, listener });
                return prefix;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static void RemoveMonoLoopbackPrefix(HttpListener listener, int port, object localhostPrefix)
        {
            if (listener == null || localhostPrefix == null || s_MonoEndPointManager == null)
                return;
            try
            {
                var endpointListener = GetMonoLoopbackEndPointListener(port);
                if (endpointListener == null)
                    return;
                var removePrefix = endpointListener.GetType().GetMethod("RemovePrefix", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                removePrefix?.Invoke(endpointListener, new object[] { localhostPrefix, listener });
            }
            catch (Exception)
            {
                // already closed or unregistered
            }
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
            int port;
            string token;
            string startedAt;
            lock (s_Lock)
            {
                if (s_Listener == null)
                    return;
                port = s_Port;
                token = s_Token;
                startedAt = s_StartedAt;
            }
            var now = DateTime.UtcNow.ToString("o");
            var descriptor = new JObject
            {
                ["pid"] = s_Pid,
                ["port"] = port,
                ["projectPath"] = ProjectPath,
                ["projectName"] = Path.GetFileName(ProjectPath),
                ["unityVersion"] = UnityVersion,
                ["mode"] = Mode,
                ["startedAt"] = startedAt,
                ["lastHeartbeat"] = now,
                ["evalToken"] = token,
                ["capabilities"] = new JArray(s_Capabilities),
                ["role"] = "main",
                ["instanceName"] = "Main Editor",
                ["endpoints"] = new JArray(new JObject { ["type"] = "http", ["port"] = port.ToString() }),
            };
            lock (s_DescriptorLock)
            {
                if (!IsRunning)
                    return;
                // Reported only once written: a heartbeat the CLI cannot read must not look fresh in /api/status.
                if (DescriptorFile.Write(s_DescriptorPath, descriptor))
                    s_LastHeartbeat = now;
            }
        }

        // Idempotent: also runs on every domain reload and on quit, whether or not the server was started.
        public static void Stop() => Stop(forReload: false);

        internal static void Stop(bool forReload)
        {
            if (s_IsSecondaryProcess)
                return;
            HttpListener listener;
            Timer heartbeat;
            CancellationTokenSource shutdown;
            int port;
            object localhostPrefix;
            lock (s_Lock)
            {
                heartbeat = s_Heartbeat;
                s_Heartbeat = null;
                listener = s_Listener;
                s_Listener = null;
                port = s_Port;
                localhostPrefix = s_LocalhostPrefix;
                s_LocalhostPrefix = null;
                shutdown = s_Shutdown;
                s_Shutdown = null;
            }
            heartbeat?.Dispose();
            if (!forReload)
            {
                UnhookLifecycle();
                PlayModeCommands.RestoreRunInBackground();
                JobManager.Live.ForgetSaved();
                ConsoleCommands.ForgetSaved();
#if UPIPE_TEST_FRAMEWORK
                TestCommands.ForgetSaved();
#endif
            }
            DetachModules(forReload, serverStopped: !forReload);
            if (!forReload)
                JobManager.Live.CancelAll(stopAccepting: true);
            CancelQuietly(shutdown);
            // Queued work dies with the domain (retryable) or stops with the server (non-retryable): fail it
            // so waiting HTTP handlers answer before the listener closes connections.
            MainThreadQueue.FailPending(forReload);
            lock (s_DescriptorLock)
            {
                s_LastHeartbeat = null;
                DescriptorFile.DeleteIfOwned(s_DescriptorPath, s_Pid);
            }
            var deadline = DateTime.UtcNow.AddMilliseconds(ReloadDrainMilliseconds);
            while (Volatile.Read(ref s_RequestsInFlight) > 0 && DateTime.UtcNow < deadline)
                Thread.Sleep(10);
            try
            {
                RemoveMonoLoopbackPrefix(listener, port, localhostPrefix);
                // Abort drops lingering keep-alive sockets so Mono's EndPointListener frees the port immediately on reload.
                listener?.Abort();
            }
            catch (Exception)
            {
                // already closed
            }
        }

        static void Handle(HttpListenerContext context)
        {
            Interlocked.Increment(ref s_RequestsInFlight);
            try
            {
                var (status, body) = Route(context.Request);
                Send(context.Response, status, body);
            }
            // A thread abort means the domain is unloading: let it unwind untouched.
            catch (Exception e) when (!(e is ThreadAbortException))
            {
                // Once the server stops, the domain starts to unload, or the client disconnects mid-stream, errors
                // here are closed sockets/listeners; logging from this thread during unload can also crash the Editor.
                if (!IsClientDisconnect(e) && IsRunning && !MainThreadQueue.DomainReloadInProgress)
                    Debug.LogException(e);
                try
                {
                    Send(context.Response, 500, Failure("Internal Server Error", "Unexpected server error; see the Unity console."));
                }
                catch (Exception)
                {
                    // client went away or response stream was already started/closed
                }
            }
            finally
            {
                try
                {
                    context.Response.Close();
                }
                catch (Exception)
                {
                    // already closed or client disconnected
                }
                Interlocked.Decrement(ref s_RequestsInFlight);
            }
        }

        static bool IsClientDisconnect(Exception e)
        {
            for (var cur = e; cur != null; cur = cur.InnerException)
            {
                if (cur is HttpListenerException || cur is IOException || cur is SocketException || cur is ObjectDisposedException)
                    return true;
            }
            return false;
        }

        internal static bool IsAllowedHost(string host, int port) =>
            string.Equals(host, $"127.0.0.1:{port}", StringComparison.Ordinal) ||
            string.Equals(host, $"localhost:{port}", StringComparison.OrdinalIgnoreCase);

        static (int, JObject) Route(HttpListenerRequest request)
        {
            var remote = request.RemoteEndPoint;
            if (remote == null || !IPAddress.IsLoopback(remote.Address))
                return (403, Failure("Forbidden", "Only loopback clients are accepted."));
            // Pinning Host defeats DNS rebinding, which a loopback check alone cannot see.
            // The official unity CLI switches Host from 127.0.0.1:<port> to localhost:<port> after ECONNREFUSED during reloads.
            if (!IsAllowedHost(request.Headers["Host"], s_Port))
                return (400, Failure("Bad Request", $"Host must be 127.0.0.1:{s_Port} or localhost:{s_Port}."));
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
                    return CatalogReply(MainThreadQueue.Settled, request.QueryString);
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

        const string SettlingDetails = "The Editor is still importing or compiling after startup. Retry once /api/status reports ready.";

        // The unity CLI never reads the body of /api/status: it calls an Editor `ready` when /api/commands answers 2xx
        // and `starting` (what `status --until-ready` keeps waiting on) when it answers 503.
        internal static (int, JObject) CatalogReply(bool settled, NameValueCollection query)
        {
            return settled ? Catalog(query) : (503, Busy("settling", SettlingDetails));
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
                return (503, WithCommand(Busy("settling", SettlingDetails), command, echoCommand));
            return null;
        }

        // Main-thread commands go through the queue; the others run on the thread pool, or on a thread of their own
        // for a job, which may run for a long time. `started` runs right before the handler; `progress` is a job's reporter.
        static Task<object> StartCommand(CommandEntry command, JObject values, CancellationToken token, Action started, bool ownThread,
            Action<string, int?> progress = null)
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
                return command.CancellableHandler != null ? command.CancellableHandler(values, new CommandContext(token, progress)) : command.Handler(values);
            };
            if (!ownThread)
                return Task.Run(work, token);
            var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Increment(ref s_LiveJobThreads);
            try
            {
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
                    finally
                    {
                        Interlocked.Decrement(ref s_LiveJobThreads);
                    }
                }) { IsBackground = true, Name = "Upipe.Job" }.Start();
            }
            catch (Exception)
            {
                Interlocked.Decrement(ref s_LiveJobThreads);
                throw;
            }
            return completion.Task;
        }

        // `"job": true`: answers with the job id at once. The CLI sends timeout 2147483647 with --detach, so only the
        // command's own timeout-ms / timeout parameters bound a job; without them it runs until it ends or is cancelled.
        static (int, JObject) Detach(CommandEntry command, BindResult bound, bool echoCommand)
        {
            var refusal = Refusal(command, bound, echoCommand);
            if (refusal != null)
                return refusal.Value;
            var shutdown = Volatile.Read(ref s_Shutdown);
            if (shutdown == null || shutdown.IsCancellationRequested)
                return StopFailureReply(command, echoCommand, bound, echoParameters: false);
            if (!command.MainThreadRequired && Volatile.Read(ref s_LiveJobThreads) >= JobManager.MaxUnfinishedJobs)
                return (429, WithCommand(Failure("Too Many Requests",
                    $"{JobManager.MaxUnfinishedJobs} job worker threads are still running; wait for hung jobs to exit."), command, echoCommand));
            var timeout = TimeoutMilliseconds(null, bound.Values, 0);
            JObject job;
            try
            {
                job = JobManager.Live.Submit(command.FullName, timeout > 0 ? timeout : (int?)null,
                    (token, started, progress) => StartCommand(command, bound.Values, token, started, ownThread: true, progress));
            }
            catch (EditorReloadingException e)
            {
                return (503, WithCommand(Busy("domain_reload", e.Message), command, echoCommand));
            }
            catch (ServerStoppedException e)
            {
                return (400, WithCommand(Failure("Server Stopped", e.Message), command, echoCommand));
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

            var shutdown = Volatile.Read(ref s_Shutdown);
            if (shutdown == null || shutdown.IsCancellationRequested)
                return StopFailureReply(command, echoCommand, bound, echoParameters);

            using (var timedOut = new CancellationTokenSource())
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, timedOut.Token))
            {
                var task = StartCommand(command, bound.Values, linked.Token, null, ownThread: false);
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
                    if (inner is ServerStoppedException)
                    {
                        reply = Failure("Server Stopped", inner.Message);
                        if (echoParameters)
                            reply["parameters"] = bound.Values;
                        return (400, WithCommand(reply, command, echoCommand));
                    }
                    if (inner is OperationCanceledException && shutdown.IsCancellationRequested)
                        return StopFailureReply(command, echoCommand, bound, echoParameters);
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
                    if (shutdown.IsCancellationRequested)
                        return StopFailureReply(command, echoCommand, bound, echoParameters);
                    // Still queued: cancelling guarantees it never runs. Already running: it cannot be stopped.
                    CancelQuietly(timedOut);
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

        internal static (int, JObject) StopFailureReply(CommandEntry command, bool echoCommand, BindResult bound, bool echoParameters)
        {
            if (MainThreadQueue.DomainReloadInProgress)
            {
                // `unity recompile` (1.0.0-beta.13) aborts on HTTP 503 while polling `recompile_status`; serve the
                // current snapshot as 200 during reload teardown so CLI keeps polling, while other commands stay 503.
                if (command.FullName == "script recompile_status")
                {
                    var compiling = new JObject { ["success"] = true };
                    WithCommand(compiling, command, echoCommand);
                    compiling["result"] = RecompileCommands.Current;
                    if (echoParameters && bound != null)
                        compiling["parameters"] = bound.Values;
                    return (200, compiling);
                }
                return (503, WithCommand(Busy("domain_reload", new EditorReloadingException().Message), command, echoCommand));
            }
            var reply = Failure("Server Stopped", new ServerStoppedException().Message);
            if (echoParameters && bound != null)
                reply["parameters"] = bound.Values;
            return (400, WithCommand(reply, command, echoCommand));
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

        // Cancel() runs the token's callbacks on this thread, and one a script registered may throw; that must not keep
        // the caller from answering, stopping the server or cancelling the other jobs.
        internal static void CancelQuietly(CancellationTokenSource source)
        {
            try
            {
                source?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (AggregateException e)
            {
                Debug.LogWarning("[Upipe] A cancellation callback threw: " + Unwrap(e).Message);
            }
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
            // Close each connection after its reply so reload teardown never waits on or races with pooled keep-alive sockets.
            response.KeepAlive = false;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.OutputStream.Close();
        }
    }
}
