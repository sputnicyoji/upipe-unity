using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Upipe.Editor.Tests
{
    public class UpipeServerTests
    {
        [Test]
        public void TimeoutOrderIsRequestThenMillisecondsThenSecondsThenDefault()
        {
            var both = new JObject { ["timeout"] = "3", ["timeout-ms"] = "1500" };
            Assert.AreEqual(7000, UpipeServer.TimeoutMilliseconds(7000, both, 30));
            Assert.AreEqual(1500, UpipeServer.TimeoutMilliseconds(null, both, 30));
            Assert.AreEqual(3000, UpipeServer.TimeoutMilliseconds(null, new JObject { ["timeout"] = "3" }, 30));
            Assert.AreEqual(30000, UpipeServer.TimeoutMilliseconds(null, new JObject { ["timeout"] = "0" }, 30));
            Assert.AreEqual(int.MaxValue / 1000 * 1000, UpipeServer.TimeoutMilliseconds(null, new JObject { ["timeout"] = "99999999999" }, 30));
        }

        // `unity status --until-ready` follows the catalog route, not /api/status: 503 there keeps it waiting.
        [Test]
        public void CatalogAnswersBusyWhileSettlingAndTheCatalogOnceSettled()
        {
            var (busyStatus, busy) = UpipeServer.CatalogReply(false, new NameValueCollection());
            Assert.AreEqual(503, busyStatus);
            Assert.AreEqual("settling", (string)busy["busyReason"]);
            Assert.IsTrue((bool)busy["retryable"]);
            Assert.IsNull(busy["commands"]);

            var (okStatus, catalog) = UpipeServer.CatalogReply(true, new NameValueCollection());
            Assert.AreEqual(200, okStatus);
            Assert.IsNotNull(catalog["commands"]);
        }

        [Test]
        public void VersionMatchesPackageManifest()
        {
            var expected = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UpipeServer).Assembly)?.version;
            Assert.IsFalse(string.IsNullOrEmpty(expected), "PackageInfo did not resolve com.upipe.server");
            Assert.AreEqual(expected, UpipeServer.Version);
            Assert.AreEqual("Upipe " + expected, (string)BuiltInCommands.EditorStatus()["server"]);
            // Mounted under Assets/ (tools/mount) PackageInfo finds nothing and the fallback is what clients see.
            Assert.AreEqual(expected, UpipeServer.FallbackVersion, "bump the fallback with package.json");
        }

        [Test]
        public void EditorStatusReportsReloadFieldsAndPlayerLoopOnlyInPlayMode()
        {
            var status = BuiltInCommands.EditorStatus();
            foreach (var key in new[] { "domainReloadInProgress", "autoRefresh", "runInBackground" })
                Assert.IsTrue(status.ContainsKey(key), key);
            Assert.IsFalse((bool)status["domainReloadInProgress"]);
            Assert.AreEqual("stopped", (string)status["playMode"]);
            Assert.IsFalse(status.ContainsKey("frameCount"), "frameCount outside play mode");
            Assert.IsFalse(status.ContainsKey("playerLoopTicking"), "playerLoopTicking outside play mode");
        }

        [Test]
        public void StopRemovesTheDescriptorAndStartRestoresIt()
        {
            Assume.That(UpipeServer.IsRunning, "the server is disabled in this Editor");
#if UPIPE_TEST_FRAMEWORK
            // Stopping would close the connection of the client that started this run with `test run` and polls it.
            Assume.That(!TestCommands.IsRunning, "these tests run through Upipe's own test run");
#endif
            var descriptor = DescriptorFile.PathFor(UpipeServer.ProjectPath);
            var port = (int)JObject.Parse(File.ReadAllText(descriptor))["port"];
            try
            {
                UpipeServer.Stop();
                Assert.IsFalse(UpipeServer.IsRunning);
                Assert.IsFalse(File.Exists(descriptor), "descriptor left behind");
                Assert.IsFalse(CanConnect(port), "port still accepting");
            }
            finally
            {
                UpipeServer.Start();
            }
            Assert.IsTrue(UpipeServer.IsRunning);
            Assert.AreEqual(port, (int)JObject.Parse(File.ReadAllText(descriptor))["port"]);
            Assert.IsTrue(CanConnect(port));
        }

        // Windows keeps the old domain's socket bound briefly after a reload; moving to another port would strand a
        // CLI that is polling this one.
        [Test]
        public void StartWaitsForItsPortWhileItIsBrieflyBusy()
        {
            Assume.That(UpipeServer.IsRunning, "the server is disabled in this Editor");
#if UPIPE_TEST_FRAMEWORK
            Assume.That(!TestCommands.IsRunning, "these tests run through Upipe's own test run");
#endif
            var descriptor = DescriptorFile.PathFor(UpipeServer.ProjectPath);
            var port = (int)JObject.Parse(File.ReadAllText(descriptor))["port"];
            TcpListener squatter = null;
            try
            {
                UpipeServer.Stop();
                squatter = new TcpListener(IPAddress.Loopback, port);
                squatter.Start();
                Task.Delay(300).ContinueWith(_ => squatter.Stop());
                UpipeServer.Start();
            }
            finally
            {
                squatter?.Stop();
                if (!UpipeServer.IsRunning)
                    UpipeServer.Start();
            }
            Assert.IsTrue(UpipeServer.IsRunning);
            Assert.AreEqual(port, (int)JObject.Parse(File.ReadAllText(descriptor))["port"]);
        }

        // Several Editors share 7800-7849: when the port this session used is taken, the server moves to a free one.
        [Test]
        public void ATakenPortMovesTheServerToAFreeOne()
        {
            Assume.That(UpipeServer.IsRunning, "the server is disabled in this Editor");
#if UPIPE_TEST_FRAMEWORK
            Assume.That(!TestCommands.IsRunning, "these tests run through Upipe's own test run");
#endif
            var descriptor = DescriptorFile.PathFor(UpipeServer.ProjectPath);
            var port = (int)JObject.Parse(File.ReadAllText(descriptor))["port"];
            var blocker = new TcpListener(IPAddress.Loopback, port);
            int moved;
            try
            {
                UpipeServer.Stop();
                blocker.Start();
                UpipeServer.Start();
                Assert.IsTrue(UpipeServer.IsRunning);
                moved = (int)JObject.Parse(File.ReadAllText(descriptor))["port"];
                Assert.AreNotEqual(port, moved);
                Assert.IsTrue(CanConnect(moved));
            }
            finally
            {
                UpipeServer.Stop();
                blocker.Stop();
                UnityEditor.SessionState.SetInt("Upipe.Port", port); // the port this Editor had before the test
                UpipeServer.Start();
            }
            Assert.AreEqual(port, (int)JObject.Parse(File.ReadAllText(descriptor))["port"]);
        }

#if UPIPE_TEST_FRAMEWORK
        [Test]
        public void TestCommandsDetachDestroysNativeTestRunnerApiAndAttachRecreatesIt()
        {
            Assume.That(UpipeServer.IsRunning, "the server is disabled in this Editor");
            Assume.That(!TestCommands.IsRunning, "these tests run through Upipe's own test run");
            Assert.IsTrue(TestCommands.HasApi);
            try
            {
                TestCommands.Detach();
                Assert.IsFalse(TestCommands.HasApi);
            }
            finally
            {
                TestCommands.Attach();
            }
            Assert.IsTrue(TestCommands.HasApi);
        }
#endif

        [Test]
        public void StaleDescriptorIsRemovedOnlyWhenItsWriterHasExited()
        {
            var path = Path.Combine(Path.GetTempPath(), "upipe-descriptor-test-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                // Written by a live process that is not the caller: this Editor, seen from a caller with pid -1.
                File.WriteAllText(path, "{\"pid\": " + System.Diagnostics.Process.GetCurrentProcess().Id + "}");
                DescriptorFile.DeleteIfOwned(path, -1, orStale: true);
                Assert.IsTrue(File.Exists(path), "removed a live instance's descriptor");
                File.WriteAllText(path, "{\"pid\": 2147483647}"); // no such process
                DescriptorFile.DeleteIfOwned(path, -1);
                Assert.IsTrue(File.Exists(path), "removed a stale descriptor without orStale");
                DescriptorFile.DeleteIfOwned(path, -1, orStale: true);
                Assert.IsFalse(File.Exists(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void ManualStopDrainsInFlightRequestsWithNonRetryableError()
        {
            Assume.That(UpipeServer.IsRunning, "the server is disabled in this Editor");
#if UPIPE_TEST_FRAMEWORK
            Assume.That(!TestCommands.IsRunning, "these tests run through Upipe's own test run");
#endif
            var savedQueue = MainThreadQueue.CaptureTestState();
            if (!MainThreadQueue.Settled)
                MainThreadQueue.ResetForTests(settled: true, coldSettled: true, lastTickUtcTicks: DateTime.UtcNow.Ticks);

            var descriptor = JObject.Parse(File.ReadAllText(DescriptorFile.PathFor(UpipeServer.ProjectPath)));
            var port = (int)descriptor["port"];
            var token = (string)descriptor["evalToken"];
            Task<(int status, JObject body)> replyTask = null;
            string jobId = null;
            try
            {
                var (jobStatus, jobReply) = PostExec(port, token, "{\"argv\":[\"console\",\"clear\"],\"job\":true}");
                Assert.AreEqual(200, jobStatus);
                jobId = (string)jobReply["result"]["jobId"];

                // Run on a worker thread: the main thread stays inside this [Test] method, so the queued command sits until Stop().
                replyTask = Task.Run(() => PostExec(port, token, "{\"argv\":[\"console\",\"clear\"]}"));
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (MainThreadQueue.PendingCount < 2 && !replyTask.IsCompleted && DateTime.UtcNow < deadline)
                    Thread.Sleep(5);
                Assert.GreaterOrEqual(MainThreadQueue.PendingCount, 2, "the HTTP request never reached MainThreadQueue");

                UpipeServer.Stop();
                Assert.IsTrue(replyTask.Wait(2000), "Stop() closed the listener before the error response finished sending");
                var (status, body) = replyTask.Result;
                Assert.AreEqual(400, status);
                Assert.AreEqual("Server Stopped", (string)body["error"]);
                Assert.IsFalse((bool?)body["retryable"] ?? false, "manual stop must not be retryable");

                var stoppedJob = JobManager.Live.Status(jobId);
                Assert.IsNotNull(stoppedJob);
                Assert.AreEqual(JobManager.Canceled, (string)stoppedJob["state"]);
                Assert.IsTrue((bool)stoppedJob["cancellationRequested"]);
            }
            finally
            {
                UpipeServer.Start();
                MainThreadQueue.RestoreTestState(savedQueue);
                MainThreadQueue.Pump();
            }
        }

        [Test]
        public void ExitedProcessPidIsNotTreatedAsRunning()
        {
            var fileName = Application.platform == RuntimePlatform.WindowsEditor ? "cmd.exe" : "/usr/bin/true";
            var arguments = Application.platform == RuntimePlatform.WindowsEditor ? "/c exit 0" : "";
            int pid;
            using (var process = Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = false, CreateNoWindow = true }))
            {
                Assert.IsNotNull(process);
                pid = process.Id;
                Assert.IsTrue(process.WaitForExit(5000), "helper process did not exit");
                Assert.IsFalse(DescriptorFile.IsRunning(pid), "exited process still seen via Handle/zombie must read as not running");
            }
            Assert.IsFalse(DescriptorFile.IsRunning(pid));
        }

        // Owner-only both as a new file and over a descriptor that inherited its folder's entries: the swap must not keep
        // the replaced file's DACL, as File.Replace (ReplaceFile) would.
        [Test]
        public void DescriptorIsOwnerOnlyOnWindowsNewOrReplacing()
        {
            Assume.That(Application.platform == RuntimePlatform.WindowsEditor, "Windows only; smoke checks 0600 elsewhere");
            // Under the project, so a new file inherits the project's ACL like the real descriptor does.
            var path = Path.Combine(UpipeServer.ProjectPath, "Temp", "upipe-acl-test-" + Guid.NewGuid().ToString("N"), ".unity-pipeline-port");
            try
            {
                Assert.IsTrue(DescriptorFile.Write(path, new JObject { ["pid"] = 1 }));
                AssertOwnerOnly(path);
                File.Delete(path);
                File.WriteAllText(path, "{}");
                StringAssert.Contains(";ID;", DaclSddl(path), "precondition: a new file inherits its folder's entries");
                Assert.IsTrue(DescriptorFile.Write(path, new JObject { ["pid"] = 2 }));
                AssertOwnerOnly(path);
                Assert.AreEqual(2, (int)JObject.Parse(File.ReadAllText(path))["pid"]);
                Assert.IsFalse(File.Exists(path + ".tmp"));
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(path), true);
            }
        }

        // Protected (P, AI is SetNamedSecurityInfo's auto-inherited flag), with one full-control entry for this user.
        static void AssertOwnerOnly(string path)
        {
            var sid = DescriptorFile.WindowsAcl.CurrentUserSid();
            StringAssert.StartsWith("S-1-5-", sid);
            StringAssert.IsMatch(@"^D:P(AI)?\(A;;FA;;;" + Regex.Escape(sid) + @"\)$", DaclSddl(path));
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern uint GetNamedSecurityInfoW(string name, int type, uint info, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr descriptor);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(IntPtr descriptor, uint revision, uint info, out IntPtr sddl, IntPtr length);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr memory);

        // The DACL in SDDL form, such as "D:PAI(A;;FA;;;S-1-5-21-...)".
        static string DaclSddl(string path)
        {
            const int seFileObject = 1;
            const uint daclSecurityInformation = 0x4;
            Assert.AreEqual(0u, GetNamedSecurityInfoW(path, seFileObject, daclSecurityInformation, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var descriptor));
            try
            {
                Assert.IsTrue(ConvertSecurityDescriptorToStringSecurityDescriptorW(descriptor, 1, daclSecurityInformation, out var sddl, IntPtr.Zero));
                try
                {
                    return Marshal.PtrToStringUni(sddl);
                }
                finally
                {
                    LocalFree(sddl);
                }
            }
            finally
            {
                LocalFree(descriptor);
            }
        }

        [Test]
        public void ResponsesCloseConnectionsToAvoidReloadKeepAliveErrors()
        {
            Assume.That(UpipeServer.IsRunning, "the server is disabled in this Editor");
            var descriptor = JObject.Parse(File.ReadAllText(DescriptorFile.PathFor(UpipeServer.ProjectPath)));
            var port = (int)descriptor["port"];
            var token = (string)descriptor["evalToken"];
            var request = (HttpWebRequest)WebRequest.Create($"http://127.0.0.1:{port}/api/status");
            request.Method = "GET";
            request.Headers["Authorization"] = "Bearer " + token;
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                Assert.AreEqual(200, (int)response.StatusCode);
                StringAssert.Contains("close", (response.Headers["Connection"] ?? "").ToLowerInvariant());
            }
        }

        [Test]
        public void RecompileStatusStaysOkDuringDomainReloadWhileOtherCommandsReturn503()
        {
            var savedQueue = MainThreadQueue.CaptureTestState();
            var savedRecompile = RecompileCommands.Current;
            var savedLastErrors = RecompileCommands.LastErrors;
            try
            {
                MainThreadQueue.MarkDomainReloading();
                RecompileCommands.OnCompilationStarted();

                var recompileStatusCmd = CommandRegistry.Find("script", "recompile_status");
                var editorStatusCmd = CommandRegistry.Find("editor", "status");
                Assert.IsNotNull(recompileStatusCmd);
                Assert.IsNotNull(editorStatusCmd);

                var (rcStatus, rcBody) = UpipeServer.StopFailureReply(recompileStatusCmd, echoCommand: true, bound: new BindResult(), echoParameters: false);
                Assert.AreEqual(200, rcStatus);
                Assert.IsTrue((bool)rcBody["success"]);
                Assert.AreEqual("compiling", (string)rcBody["result"]["status"]);

                var (edStatus, edBody) = UpipeServer.StopFailureReply(editorStatusCmd, echoCommand: false, bound: new BindResult(), echoParameters: false);
                Assert.AreEqual(503, edStatus);
                Assert.AreEqual("domain_reload", (string)edBody["busyReason"]);
                Assert.IsTrue((bool)edBody["retryable"]);
            }
            finally
            {
                RecompileCommands.ResetTestState();
                RecompileCommands.LastErrors = savedLastErrors;
                RecompileCommands.Save(savedRecompile);
                MainThreadQueue.RestoreTestState(savedQueue);
                if (savedQueue.Attached)
                    MainThreadQueue.Pump();
            }
        }

        [Test]
        public void AcceptsLocalhostHostHeaderOnLoopbackAndRejectsOtherHosts()
        {
            Assert.IsTrue(UpipeServer.IsAllowedHost("127.0.0.1:7800", 7800));
            Assert.IsTrue(UpipeServer.IsAllowedHost("localhost:7800", 7800));
            Assert.IsTrue(UpipeServer.IsAllowedHost("LOCALHOST:7800", 7800));
            Assert.IsFalse(UpipeServer.IsAllowedHost("localhost:7801", 7800));
            Assert.IsFalse(UpipeServer.IsAllowedHost("evil.example:7800", 7800));
            Assert.IsFalse(UpipeServer.IsAllowedHost("localhost.evil.example:7800", 7800));
            Assert.IsFalse(UpipeServer.IsAllowedHost("", 7800));
            Assert.IsFalse(UpipeServer.IsAllowedHost(null, 7800));

            Assume.That(UpipeServer.IsRunning, "the server is disabled in this Editor");
            var descriptor = JObject.Parse(File.ReadAllText(DescriptorFile.PathFor(UpipeServer.ProjectPath)));
            var port = (int)descriptor["port"];
            var token = (string)descriptor["evalToken"];

            var okReply = SendRawGet(port, $"localhost:{port}", token);
            StringAssert.StartsWith("HTTP/1.1 200 OK", okReply);
            StringAssert.Contains("\"status\"", okReply);

            var badReply = SendRawGet(port, $"evil.example:{port}", token);
            StringAssert.StartsWith("HTTP/1.1 400 Bad Request", badReply);
            // Mono rejects unregistered Host headers at EndPointListener ("Invalid host") before Route() runs.
            Assert.IsTrue(badReply.Contains("Host must be") || badReply.Contains("Invalid host"), badReply);
            Assert.IsFalse(badReply.Contains("\"status\""), badReply);
        }

        [Test]
        public void WindowsChildProcessDoesNotInheritListeningSocketAcrossReload()
        {
            Assume.That(Application.platform == RuntimePlatform.WindowsEditor, "Windows handle inheritance check");
            Assume.That(UpipeServer.IsRunning, "the server is disabled in this Editor");
#if UPIPE_TEST_FRAMEWORK
            Assume.That(!TestCommands.IsRunning, "these tests run through Upipe's own test run");
#endif
            var savedQueue = MainThreadQueue.CaptureTestState();
            var descriptor = DescriptorFile.PathFor(UpipeServer.ProjectPath);
            var port = (int)JObject.Parse(File.ReadAllText(descriptor))["port"];
            Process child = null;
            try
            {
                child = Process.Start(new ProcessStartInfo("ping.exe", "-n 8 127.0.0.1")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                Assert.IsNotNull(child);
                Thread.Sleep(150);
                Assert.IsFalse(child.HasExited, "helper child exited too early");

                UpipeServer.Stop(forReload: true);
                Assert.IsFalse(child.HasExited, "helper child should still be alive during reload check");
                Assert.IsFalse(CanConnect(port), "child process inherited the listening socket handle");

                UpipeServer.Start();
                Assert.IsTrue(UpipeServer.IsRunning);
                Assert.AreEqual(port, (int)JObject.Parse(File.ReadAllText(descriptor))["port"]);
            }
            finally
            {
                try
                {
                    if (child != null && !child.HasExited)
                    {
                        child.Kill();
                        child.WaitForExit(2000);
                    }
                }
                catch (Exception)
                {
                }
                child?.Dispose();
                if (!UpipeServer.IsRunning)
                    UpipeServer.Start();
                MainThreadQueue.RestoreTestState(savedQueue);
                if (savedQueue.Attached)
                    MainThreadQueue.Pump();
            }
        }

        [Test]
        public void ProjectPathIsCanonicalizedAndMatchesAssetRelativePaths()
        {
            Assert.AreEqual(UpipeServer.CanonicalizePath(UpipeServer.ProjectPath), UpipeServer.ProjectPath);
            Assert.AreEqual("Assets/Shot.png", CaptureCommands.AssetRelativePath(
                UpipeServer.ProjectPath, UpipeServer.ResolveProjectPath("Assets/Shot.png"), UpipeServer.PathsIgnoreCase));
        }

        static string SendRawGet(int port, string hostHeader, string token)
        {
            using (var client = new TcpClient())
            {
                client.ReceiveTimeout = 5000;
                client.Connect(IPAddress.Loopback, port);
                using (var stream = client.GetStream())
                {
                    var req = Encoding.ASCII.GetBytes(
                        $"GET /api/status HTTP/1.1\r\nHost: {hostHeader}\r\nAuthorization: Bearer {token}\r\nConnection: close\r\n\r\n");
                    stream.Write(req, 0, req.Length);
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        return reader.ReadToEnd();
                }
            }
        }

        static (int status, JObject body) PostExec(int port, string token, string payload)
        {
            var request = (HttpWebRequest)WebRequest.Create($"http://127.0.0.1:{port}/api/exec");
            request.Method = "POST";
            request.ContentType = "application/json; charset=utf-8";
            request.Headers["Authorization"] = "Bearer " + token;
            var bytes = Encoding.UTF8.GetBytes(payload);
            request.ContentLength = bytes.Length;
            using (var stream = request.GetRequestStream())
                stream.Write(bytes, 0, bytes.Length);
            HttpWebResponse response;
            try
            {
                response = (HttpWebResponse)request.GetResponse();
            }
            catch (WebException e) when (e.Response is HttpWebResponse err)
            {
                response = err;
            }
            using (response)
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                return ((int)response.StatusCode, JObject.Parse(reader.ReadToEnd()));
        }

        static bool CanConnect(int port)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    client.Connect(IPAddress.Loopback, port);
                    return true;
                }
            }
            catch (SocketException)
            {
                return false;
            }
        }
    }
}
