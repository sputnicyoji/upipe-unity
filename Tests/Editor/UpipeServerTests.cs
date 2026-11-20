using System.IO;
using System.Net;
using System.Net.Sockets;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

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
