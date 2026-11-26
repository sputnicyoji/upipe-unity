using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Upipe.Editor
{
    // Compiles C# with the Roslyn compiler that ships with the Editor (the one Unity compiles project scripts with),
    // in a separate process, against every assembly loaded in the Editor. Used by script eval and script run.
    internal static class CscCompiler
    {
        internal readonly struct SourceFile
        {
            public readonly string Path;
            public readonly string Content;

            public SourceFile(string path, string content)
            {
                Path = path;
                Content = content;
            }
        }

        const int CompileTimeoutMilliseconds = 120000;
        const int MaxMemoryCacheEntries = 64;
        const int MaxDiskCacheEntries = 128;

        static readonly Regex s_Diagnostic = new Regex(
            @"^(?<file>.+?)\((?<line>\d+),(?<column>\d+)\): (?<severity>error|warning) (?<id>\w+): (?<message>.*)$");
        static readonly Encoding s_Utf8NoBom = new UTF8Encoding(false);
        static readonly object s_ReferencesLock = new object();
        static readonly object s_CacheLock = new object();
        static readonly object s_DiskCacheLock = new object();
        static readonly Dictionary<string, LinkedListNode<CacheEntry>> s_MemoryCache = new Dictionary<string, LinkedListNode<CacheEntry>>(StringComparer.Ordinal);
        static readonly LinkedList<CacheEntry> s_MemoryLru = new LinkedList<CacheEntry>();

        sealed class CacheEntry
        {
            public readonly string Key;
            public readonly byte[] Assembly;
            public readonly byte[] Symbols;

            public CacheEntry(string key, byte[] assembly, byte[] symbols)
            {
                Key = key;
                Assembly = assembly;
                Symbols = symbols;
            }
        }

        static string s_Dotnet;
        static string s_Compiler;
        static bool s_AssemblyLoadHooked;
        static int s_ReferenceEpoch;
        static int s_CachedEpoch = -1;
        static string[] s_CachedReferences;
        static string s_CachedReferencesHash;
        static long s_CacheHits;
        static long s_CacheMisses;

        public static string WorkDirectory { get; private set; }
        public static string[] EditorDefines { get; private set; } = Array.Empty<string>();
        internal static long CacheHits => Volatile.Read(ref s_CacheHits);
        internal static long CacheMisses => Volatile.Read(ref s_CacheMisses);

        internal sealed class Error
        {
            public string File;
            public int Line;
            public int Column; // 1-based, as csc prints it
            public string Id;
            public string Message;
            public string Raw;

            // As the official package prints a diagnostic: 1-based line, 0-based column.
            public string LineColumnText => $"line {Line}, col {Column - 1}: error {Id}: {Message}";
        }

        // Main thread: reads Editor paths and active script compilation defines.
        public static void Initialize()
        {
            var contents = EditorApplication.applicationContentsPath;
            var windows = Application.platform == RuntimePlatform.WindowsEditor;
            s_Dotnet = Path.Combine(contents, "NetCoreRuntime", windows ? "dotnet.exe" : "dotnet");
            s_Compiler = Path.Combine(contents, "DotNetSdkRoslyn", "csc.dll");
            WorkDirectory = Path.Combine(UpipeServer.ProjectPath, "Temp", "UpipeEval");
            EditorDefines = EditorUserBuildSettings.activeScriptCompilationDefines ?? Array.Empty<string>();
        }

        // Returns the assembly and its portable PDB, or the compile errors (assembly null). Throws CommandFailure
        // when the compiler itself fails. `noWarn` lists warning numbers that the generated wrapper code triggers.
        // `defines` are the preprocessor symbols the code is compiled with.
        public static (byte[] assembly, byte[] symbols, List<Error> errors) Compile(string name, string source, string noWarn,
            IEnumerable<string> defines = null, CancellationToken cancel = default) =>
            CompileFiles(name, new[] { new SourceFile(null, source) }, noWarn, defines, cancel);

        // Compiles one or more sources together without locking the originals on disk. A source with a Path starts with
        // `#line 1 "<path>"`, so diagnostics and Portable PDB stack traces name the real file and its line numbers.
        // `cancel` (a request timeout or a job cancel) kills the compiler and throws OperationCanceledException.
        public static (byte[] assembly, byte[] symbols, List<Error> errors) CompileFiles(string name,
            IReadOnlyList<SourceFile> sources, string noWarn, IEnumerable<string> defines = null, CancellationToken cancel = default)
        {
            if (sources == null || sources.Count == 0)
                throw new ArgumentException("At least one source file is required.", nameof(sources));
            cancel.ThrowIfCancellationRequested();

            var symbolsList = NormalizeDefines(defines);
            var (references, referencesHash) = GetReferencesWithHash();
            var cacheKey = ComputeCacheKey(name, sources, noWarn ?? "", symbolsList, referencesHash);
            if (TryGetCached(cacheKey, out var cachedAssembly, out var cachedSymbols))
            {
                Interlocked.Increment(ref s_CacheHits);
                return (cachedAssembly, cachedSymbols, new List<Error>());
            }
            Interlocked.Increment(ref s_CacheMisses);

            Directory.CreateDirectory(WorkDirectory);
            var basePath = Path.Combine(WorkDirectory, $"{name}_{Guid.NewGuid():N}");
            var rspPath = basePath + ".rsp";
            var dllPath = basePath + ".dll";
            var pdbPath = basePath + ".pdb";
            var units = Enumerable.Range(0, sources.Count).Select(i => $"{basePath}_{i}.cs").ToList();
            var tempFiles = units.Concat(new[] { rspPath, dllPath, pdbPath }).ToList();
            try
            {
                for (var i = 0; i < sources.Count; i++)
                {
                    var realPath = (sources[i].Path ?? "").Replace("\"", "");
                    var content = (sources[i].Content ?? "").TrimStart('\uFEFF');
                    File.WriteAllText(units[i], realPath.Length > 0 ? $"#line 1 \"{realPath}\"\n{content}" : content, s_Utf8NoBom);
                }
                var arguments = new StringBuilder();
                arguments.AppendLine("-nologo").AppendLine("-noconfig").AppendLine("-nostdlib+").AppendLine("-target:library")
                    .AppendLine("-langversion:9.0").AppendLine("-debug:portable").AppendLine("-optimize-")
                    .AppendLine("-utf8output").AppendLine("-codepage:65001")
                    // Diagnostics in English whatever the machine's language, as the official package reports them.
                    .AppendLine("-preferreduilang:en-US");
                if (!string.IsNullOrEmpty(noWarn))
                    arguments.AppendLine("-nowarn:" + noWarn);
                if (symbolsList.Count > 0)
                    arguments.AppendLine("-define:" + string.Join(";", symbolsList));
                arguments
                    .AppendLine($"-out:\"{dllPath}\"");
                foreach (var reference in references)
                    arguments.AppendLine($"-r:\"{reference}\"");
                foreach (var unit in units)
                    arguments.AppendLine($"\"{unit}\"");
                File.WriteAllText(rspPath, arguments.ToString(), s_Utf8NoBom);

                var (exitCode, output) = Run(rspPath, cancel);
                var errors = new List<Error>();
                foreach (var line in output.Split('\n').Select(l => l.TrimEnd('\r')))
                {
                    var match = s_Diagnostic.Match(line);
                    if (!match.Success || match.Groups["severity"].Value != "error")
                        continue;
                    errors.Add(new Error
                    {
                        File = match.Groups["file"].Value,
                        Line = int.Parse(match.Groups["line"].Value),
                        Column = int.Parse(match.Groups["column"].Value),
                        Id = match.Groups["id"].Value,
                        Message = match.Groups["message"].Value,
                        Raw = line,
                    });
                }
                if (errors.Count > 0)
                    return (null, null, errors);
                if (exitCode != 0 || !File.Exists(dllPath))
                    throw new CommandFailure("Command Execution Failed", "EVAL_INTERNAL_ERROR: the compiler failed: " + output.Trim());
                var compiledAssembly = File.ReadAllBytes(dllPath);
                var compiledSymbols = File.Exists(pdbPath) ? File.ReadAllBytes(pdbPath) : null;
                StoreCached(cacheKey, compiledAssembly, compiledSymbols);
                return (compiledAssembly, compiledSymbols, errors);
            }
            finally
            {
                foreach (var path in tempFiles)
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (Exception)
                    {
                        // Temp/ is cleared by Unity at the next launch anyway
                    }
                }
            }
        }

        static (int exitCode, string output) Run(string responseFile, CancellationToken cancel)
        {
            var start = new ProcessStartInfo(s_Dotnet, $"\"{s_Compiler}\" @\"{responseFile}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
            };
            start.EnvironmentVariables["DOTNET_CLI_UI_LANGUAGE"] = "en";
            using (var process = Process.Start(start))
            using (cancel.Register(() => Kill(process)))
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(CompileTimeoutMilliseconds))
                {
                    Kill(process);
                    throw new CommandFailure("Command Execution Failed", "EVAL_INTERNAL_ERROR: the compiler did not finish in time.");
                }
                cancel.ThrowIfCancellationRequested();
                return (process.ExitCode, stdout.Result + stderr.Result);
            }
        }

        static void Kill(Process process)
        {
            try
            {
                process.Kill();
            }
            catch (Exception)
            {
                // already exited
            }
        }

        internal static bool TryCompileFromCache(string name, string source, string noWarn,
            IEnumerable<string> defines, out byte[] assembly, out byte[] symbols)
        {
            var symbolsList = NormalizeDefines(defines);
            var (_, referencesHash) = GetReferencesWithHash();
            var cacheKey = ComputeCacheKey(name, new[] { new SourceFile(null, source) }, noWarn ?? "", symbolsList, referencesHash);
            if (!TryGetCached(cacheKey, out assembly, out symbols))
                return false;
            Interlocked.Increment(ref s_CacheHits);
            return true;
        }

        static List<string> NormalizeDefines(IEnumerable<string> defines) =>
            (defines ?? Enumerable.Empty<string>())
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => d.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(d => d, StringComparer.Ordinal)
                .ToList();

        internal static string ShortHash(string text)
        {
            using (var sha = SHA256.Create())
            {
                AddBytes(sha, text);
                return FinishHex(sha, 8);
            }
        }

        internal static void ClearCache(bool includeDisk = false)
        {
            lock (s_CacheLock)
            {
                s_MemoryCache.Clear();
                s_MemoryLru.Clear();
            }
            Interlocked.Exchange(ref s_CacheHits, 0);
            Interlocked.Exchange(ref s_CacheMisses, 0);
            if (!includeDisk)
                return;
            var cacheDir = DiskCacheDirectory();
            if (string.IsNullOrEmpty(cacheDir) || !Directory.Exists(cacheDir))
                return;
            foreach (var file in Directory.EnumerateFiles(cacheDir))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception)
                {
                    // Ignore locked or racing temp files
                }
            }
        }

        static string DiskCacheDirectory() => string.IsNullOrEmpty(WorkDirectory) ? null : Path.Combine(WorkDirectory, "cache");

        static string ComputeCacheKey(string name, IReadOnlyList<SourceFile> sources, string noWarn,
            IReadOnlyList<string> defines, string referencesHash)
        {
            using (var sha = SHA256.Create())
            {
                AddBytes(sha, "upipe-csc-v1\n");
                AddBytes(sha, (name ?? "") + "\n");
                AddBytes(sha, (noWarn ?? "") + "\n");
                AddBytes(sha, string.Join(";", defines) + "\n");
                AddBytes(sha, (referencesHash ?? "") + "\n");
                AddBytes(sha, sources.Count + "\n");
                for (var i = 0; i < sources.Count; i++)
                {
                    var realPath = (sources[i].Path ?? "").Replace("\"", "");
                    var content = (sources[i].Content ?? "").TrimStart('\uFEFF');
                    AddBytes(sha, realPath + "\0" + content.Length + "\0");
                    AddBytes(sha, content + "\n");
                }
                return FinishHex(sha);
            }
        }

        static string ComputeReferencesHash(string[] references)
        {
            using (var sha = SHA256.Create())
            {
                AddBytes(sha, "refs-v1\n");
                AddBytes(sha, (s_Compiler ?? "") + "\n");
                foreach (var path in references.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    AddBytes(sha, path);
                    try
                    {
                        var info = new FileInfo(path);
                        AddBytes(sha, $"|{info.Length}|{info.LastWriteTimeUtc.Ticks}\n");
                    }
                    catch (Exception)
                    {
                        AddBytes(sha, "|0|0\n");
                    }
                }
                return FinishHex(sha);
            }
        }

        static void AddBytes(SHA256 sha, string text)
        {
            var bytes = s_Utf8NoBom.GetBytes(text ?? "");
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        static string FinishHex(SHA256 sha, int bytesLimit = 0)
        {
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var hash = sha.Hash;
            var count = bytesLimit > 0 ? Math.Min(bytesLimit, hash.Length) : hash.Length;
            var sb = new StringBuilder(count * 2);
            for (var i = 0; i < count; i++)
                sb.Append(hash[i].ToString("x2"));
            return sb.ToString();
        }

        static bool TryGetCached(string key, out byte[] assembly, out byte[] symbols)
        {
            lock (s_CacheLock)
            {
                if (s_MemoryCache.TryGetValue(key, out var node))
                {
                    s_MemoryLru.Remove(node);
                    s_MemoryLru.AddFirst(node);
                    assembly = node.Value.Assembly;
                    symbols = node.Value.Symbols;
                    return true;
                }
            }

            var cacheDir = DiskCacheDirectory();
            if (!string.IsNullOrEmpty(cacheDir))
            {
                var dllPath = Path.Combine(cacheDir, key + ".dll");
                var pdbPath = Path.Combine(cacheDir, key + ".pdb");
                try
                {
                    if (File.Exists(dllPath))
                    {
                        assembly = File.ReadAllBytes(dllPath);
                        symbols = File.Exists(pdbPath) ? File.ReadAllBytes(pdbPath) : null;
                        if (assembly.Length > 0)
                        {
                            try
                            {
                                File.SetLastWriteTimeUtc(dllPath, DateTime.UtcNow);
                            }
                            catch (Exception)
                            {
                                // best-effort LRU touch
                            }
                            StoreInMemory(key, assembly, symbols);
                            return true;
                        }
                    }
                }
                catch (Exception)
                {
                    // Fall through to recompilation on corrupt/raced disk read
                }
            }

            assembly = null;
            symbols = null;
            return false;
        }

        static void StoreCached(string key, byte[] assembly, byte[] symbols)
        {
            if (assembly == null || assembly.Length == 0)
                return;
            StoreInMemory(key, assembly, symbols);

            var cacheDir = DiskCacheDirectory();
            if (string.IsNullOrEmpty(cacheDir))
                return;
            lock (s_DiskCacheLock)
            {
                try
                {
                    Directory.CreateDirectory(cacheDir);
                    var pdbPath = Path.Combine(cacheDir, key + ".pdb");
                    var dllPath = Path.Combine(cacheDir, key + ".dll");
                    // Write symbols before the assembly so .dll is the commit point for readers.
                    var wrotePdb = symbols != null && WriteAtomic(pdbPath, symbols);
                    if (WriteAtomic(dllPath, assembly))
                        PruneDiskCache(cacheDir);
                    else if (wrotePdb && !File.Exists(dllPath))
                        TryDeleteFile(pdbPath);
                }
                catch (Exception)
                {
                    // Disk cache is an optimization; never fail compilation over cache I/O
                }
            }
        }

        static void StoreInMemory(string key, byte[] assembly, byte[] symbols)
        {
            lock (s_CacheLock)
            {
                if (s_MemoryCache.TryGetValue(key, out var existing))
                {
                    s_MemoryLru.Remove(existing);
                    s_MemoryCache.Remove(key);
                }
                var node = new LinkedListNode<CacheEntry>(new CacheEntry(key, assembly, symbols));
                s_MemoryLru.AddFirst(node);
                s_MemoryCache[key] = node;
                while (s_MemoryCache.Count > MaxMemoryCacheEntries && s_MemoryLru.Last != null)
                {
                    var oldest = s_MemoryLru.Last;
                    s_MemoryLru.RemoveLast();
                    s_MemoryCache.Remove(oldest.Value.Key);
                }
            }
        }

        static bool WriteAtomic(string destination, byte[] data)
        {
            if (File.Exists(destination))
                return false;
            var temp = $"{destination}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(temp, data);
                if (File.Exists(destination))
                    return false;
                File.Move(temp, destination);
                return true;
            }
            catch (IOException)
            {
                // Another concurrent compile won the race
                return false;
            }
            finally
            {
                TryDeleteFile(temp);
            }
        }

        internal static void PruneDiskCache(string cacheDir, int maxEntries = MaxDiskCacheEntries)
        {
            var info = new DirectoryInfo(cacheDir);
            var dlls = info.GetFiles("*.dll");
            if (dlls.Length > maxEntries)
            {
                foreach (var obsolete in dlls.OrderBy(f => f.LastWriteTimeUtc).Take(dlls.Length - maxEntries))
                {
                    TryDeleteFile(obsolete.FullName);
                    // A .dll a reader still holds open (Windows) stays an entry, so it keeps its symbols.
                    if (!File.Exists(obsolete.FullName))
                        TryDeleteFile(Path.ChangeExtension(obsolete.FullName, ".pdb"));
                }
            }
            // Sweep any orphan symbol files left behind by a failed .dll write.
            foreach (var pdb in info.GetFiles("*.pdb"))
            {
                if (!File.Exists(Path.ChangeExtension(pdb.FullName, ".dll")))
                    TryDeleteFile(pdb.FullName);
            }
        }

        static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception)
            {
                // Ignore locked or racing temp/cache files
            }
        }

        internal static string[] GetReferences() => GetReferencesWithHash().references;

        // Caches disk references per AppDomain and invalidates only when a disk-backed assembly loads (in-memory
        // eval/script run assemblies have no location and leave the cache intact).
        static (string[] references, string hash) GetReferencesWithHash()
        {
            lock (s_ReferencesLock)
            {
                if (!s_AssemblyLoadHooked)
                {
                    AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                    s_AssemblyLoadHooked = true;
                }
            }

            while (true)
            {
                var epoch = Volatile.Read(ref s_ReferenceEpoch);
                lock (s_ReferencesLock)
                {
                    if (s_CachedReferences != null && s_CachedEpoch == epoch)
                        return (s_CachedReferences, s_CachedReferencesHash);
                }
                var computed = ComputeReferences().ToArray();
                var computedHash = ComputeReferencesHash(computed);
                if (Volatile.Read(ref s_ReferenceEpoch) != epoch)
                    continue;
                lock (s_ReferencesLock)
                {
                    if (!s_AssemblyLoadHooked)
                        return (computed, computedHash);
                    if (Volatile.Read(ref s_ReferenceEpoch) == epoch)
                    {
                        s_CachedReferences = computed;
                        s_CachedReferencesHash = computedHash;
                        s_CachedEpoch = epoch;
                        return (s_CachedReferences, s_CachedReferencesHash);
                    }
                }
            }
        }

        // When the server stops (or the domain unloads): drop the AssemblyLoad hook and the cached list; the next compile
        // hooks again and rebuilds it.
        public static void Detach()
        {
            lock (s_ReferencesLock)
            {
                if (s_AssemblyLoadHooked)
                    AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                s_AssemblyLoadHooked = false;
                s_CachedReferences = null;
                s_CachedReferencesHash = null;
                s_CachedEpoch = -1;
                // A list being computed right now must not be stored: loads while unhooked would go unseen.
                Interlocked.Increment(ref s_ReferenceEpoch);
            }
            lock (s_CacheLock)
            {
                s_MemoryCache.Clear();
                s_MemoryLru.Clear();
            }
        }

        static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            var assembly = args?.LoadedAssembly;
            if (assembly == null || assembly.IsDynamic)
                return;
            try
            {
                if (!string.IsNullOrEmpty(assembly.Location))
                    Interlocked.Increment(ref s_ReferenceEpoch);
            }
            catch (Exception)
            {
                // Location can throw NotSupportedException on dynamic or collectible assemblies.
            }
        }

        // Every assembly loaded in the Editor, so compiled code can use anything project code can; one per simple
        // name, because the compiler refuses two assemblies with the same name. Assemblies loaded from memory
        // (earlier evals) have no location and are left out.
        static IEnumerable<string> ComputeReferences()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .Select(a =>
                {
                    try
                    {
                        return a.Location;
                    }
                    catch (Exception)
                    {
                        return "";
                    }
                })
                .Where(path => !string.IsNullOrEmpty(path) && File.Exists(path))
                .GroupBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First());
        }
    }
}
