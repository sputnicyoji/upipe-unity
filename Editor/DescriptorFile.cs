using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
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
        const int ReplaceRetryCount = 4;
        const int ReplaceRetryDelayMs = 25;

        [DllImport("libc", SetLastError = true)]
        static extern int chmod(string path, int mode);

        public static string PathFor(string projectPath)
        {
            return Path.Combine(projectPath, "Library", "Pipeline", ".unity-pipeline-port");
        }

        // Writes an owner-only temp file and renames it over the target, so readers never see a partial file.
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
                ReplaceOrMoveWithRetry(temp, path);
                return true;
            }
            catch (Exception e)
            {
                TryDelete(temp); // it may already hold the token
                Debug.LogError($"[Upipe] Failed to write descriptor {path}: {e.Message}");
                return false;
            }
        }

        // Readers (CLI / antivirus / indexers on Windows) can hold a brief share lock during the 15 s heartbeat swap.
        static void ReplaceOrMoveWithRetry(string temp, string path)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        WindowsAcl.MoveOver(temp, path);
                    else if (File.Exists(path))
                        File.Replace(temp, path, null);
                    else
                        File.Move(temp, path);
                    return;
                }
                catch (IOException) when (attempt < ReplaceRetryCount)
                {
                    Thread.Sleep(ReplaceRetryDelayMs);
                }
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

        internal static bool IsRunning(int pid)
        {
            try
            {
                using (var process = Process.GetProcessById(pid))
                    return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false; // no such process
            }
            catch (Exception e) when (e is Win32Exception || e is InvalidOperationException || e is NotSupportedException)
            {
                // Access denied or process metadata unavailable: conservatively treat as still alive.
                return true;
            }
        }

        static bool RestrictToOwner(string path)
        {
            try
            {
                return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? WindowsAcl.RestrictToCurrentUser(path)
                    : chmod(path, OwnerReadWrite) == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The NTFS equivalent of 0600: a protected DACL (no inherited entries) with one full-control entry for the
        // current user. A project outside the profile otherwise inherits read access for Users / Authenticated Users.
        // Plain advapi32 calls rather than System.Security.AccessControl, which a .NET Standard Editor profile lacks.
        internal static class WindowsAcl
        {
            const int SeFileObject = 1;
            const uint DaclSecurityInformation = 0x4;
            const uint ProtectedDaclSecurityInformation = 0x80000000;
            const uint TokenQuery = 0x8;
            const int TokenUser = 1;
            const uint SddlRevision1 = 1;
            const uint MoveFileReplaceExisting = 0x1;
            const uint MoveFileWriteThrough = 0x8;

            static string s_UserSid;

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern bool MoveFileExW(string existing, string target, uint flags);

            [DllImport("kernel32.dll")]
            static extern IntPtr GetCurrentProcess();

            [DllImport("kernel32.dll")]
            static extern bool CloseHandle(IntPtr handle);

            [DllImport("kernel32.dll")]
            static extern IntPtr LocalFree(IntPtr memory);

            [DllImport("advapi32.dll")]
            static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

            [DllImport("advapi32.dll")]
            static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
            static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
            static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, IntPtr size);

            [DllImport("advapi32.dll")]
            static extern bool GetSecurityDescriptorDacl(IntPtr descriptor, out bool present, out IntPtr dacl, out bool defaulted);

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
            static extern uint SetNamedSecurityInfoW(string name, int type, uint info, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

            // "S-1-5-21-..." of the user this Editor runs as, or null if the token cannot be read.
            internal static string CurrentUserSid()
            {
                if (s_UserSid != null)
                    return s_UserSid;
                if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
                    return null;
                var buffer = IntPtr.Zero;
                try
                {
                    GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var length); // sizes the buffer
                    if (length <= 0)
                        return null;
                    buffer = Marshal.AllocHGlobal(length);
                    // TOKEN_USER starts with SID_AND_ATTRIBUTES, whose first field is the SID pointer.
                    if (!GetTokenInformation(token, TokenUser, buffer, length, out length)
                        || !ConvertSidToStringSidW(Marshal.ReadIntPtr(buffer), out var text))
                        return null;
                    try
                    {
                        return s_UserSid = Marshal.PtrToStringUni(text);
                    }
                    finally
                    {
                        LocalFree(text);
                    }
                }
                finally
                {
                    if (buffer != IntPtr.Zero)
                        Marshal.FreeHGlobal(buffer);
                    CloseHandle(token);
                }
            }

            internal static bool RestrictToCurrentUser(string path)
            {
                var sid = CurrentUserSid();
                if (sid == null
                    || !ConvertStringSecurityDescriptorToSecurityDescriptorW("D:P(A;;FA;;;" + sid + ")", SddlRevision1, out var descriptor, IntPtr.Zero))
                    return false;
                try
                {
                    return GetSecurityDescriptorDacl(descriptor, out var present, out var dacl, out _)
                        && present
                        && SetNamedSecurityInfoW(path, SeFileObject, DaclSecurityInformation | ProtectedDaclSecurityInformation,
                            IntPtr.Zero, IntPtr.Zero, dacl, IntPtr.Zero) == 0;
                }
                finally
                {
                    LocalFree(descriptor);
                }
            }

            // A rename keeps the moved file's own DACL, while File.Replace (ReplaceFile) keeps the replaced file's,
            // which for a descriptor written before owner-only existed still grants Users read access.
            // shortcut: replacing another Windows user's owner-only descriptor fails (no DELETE right on it); delete
            // it by hand, or take ownership first if shared machines need it.
            internal static void MoveOver(string temp, string path)
            {
                if (!MoveFileExW(temp, path, MoveFileReplaceExisting | MoveFileWriteThrough))
                    throw new IOException($"MoveFileEx failed with error {Marshal.GetLastWin32Error()}");
            }
        }
    }
}
