using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Upipe.Editor
{
    // The instance descriptor the unity CLI reads to discover this server:
    // <project>/Library/Pipeline/.unity-pipeline-port. It carries the bearer token, so it must be owner-only.
    internal static class DescriptorFile
    {
        const int OwnerReadWrite = 0x180; // 0600

        [DllImport("libc", SetLastError = true)]
        static extern int chmod(string path, int mode);

        public static string PathFor(string projectPath)
        {
            return Path.Combine(projectPath, "Library", "Pipeline", ".unity-pipeline-port");
        }

        // Writes a 0600 temp file and renames it over the target, so readers never see a partial file.
        public static bool Write(string path, JObject descriptor)
        {
            var temp = path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(temp, string.Empty);
                if (!RestrictToOwner(temp))
                {
                    File.Delete(temp);
                    Debug.LogError($"[Upipe] Could not restrict permissions on {temp}; descriptor not written, clients cannot connect.");
                    return false;
                }
                File.WriteAllText(temp, descriptor.ToString(Formatting.None));
                if (File.Exists(path))
                    File.Replace(temp, path, null);
                else
                    File.Move(temp, path);
                return true;
            }
            catch (Exception e)
            {
                TryDelete(temp); // it may already hold the token
                Debug.LogError($"[Upipe] Failed to write descriptor {path}: {e.Message}");
                return false;
            }
        }

        static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // nothing to clean up
            }
        }

        // Only remove a descriptor this process wrote, or with orStale one whose writer has exited (a crash or a
        // SIGTERM in batchmode leaves it behind); another live instance may own the file.
        public static void DeleteIfOwned(string path, int pid, bool orStale = false)
        {
            TryDelete(path + ".tmp");
            try
            {
                if (!File.Exists(path))
                    return;
                var owner = (int?)JObject.Parse(File.ReadAllText(path))["pid"];
                if (owner == pid || (orStale && owner.HasValue && !IsRunning(owner.Value)))
                    File.Delete(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Upipe] Failed to delete descriptor {path}: {e.Message}");
            }
        }

        static bool IsRunning(int pid)
        {
            try
            {
                using (Process.GetProcessById(pid))
                    return true;
            }
            catch (ArgumentException)
            {
                return false; // no such process
            }
        }

        static bool RestrictToOwner(string path)
        {
            // shortcut: Windows relies on the default ACL of the user's profile; add an icacls-equivalent
            // ACL change before supporting shared Windows machines.
            if (Application.platform == RuntimePlatform.WindowsEditor)
                return true;
            try
            {
                return chmod(path, OwnerReadWrite) == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
