using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Upipe.Editor
{
    // Compiles C# with the Roslyn compiler that ships with the Editor (the one Unity compiles project scripts with),
    // in a separate process, against every assembly loaded in the Editor. Used by script eval.
    internal static class CscCompiler
    {
        const int CompileTimeoutMilliseconds = 120000;

        static readonly Regex s_Diagnostic = new Regex(
            @"^(?<file>.+?)\((?<line>\d+),(?<column>\d+)\): (?<severity>error|warning) (?<id>\w+): (?<message>.*)$");

        static string s_Dotnet;
        static string s_Compiler;

        public static string WorkDirectory { get; private set; }

        public sealed class Error
        {
            public string File;
            public int Line;
            public int Column; // 1-based, as csc prints it
            public string Id;
            public string Message;
            public string Raw;
        }

        // Main thread: reads Editor paths.
        public static void Initialize()
        {
            var contents = EditorApplication.applicationContentsPath;
            var windows = Application.platform == RuntimePlatform.WindowsEditor;
            s_Dotnet = Path.Combine(contents, "NetCoreRuntime", windows ? "dotnet.exe" : "dotnet");
            s_Compiler = Path.Combine(contents, "DotNetSdkRoslyn", "csc.dll");
            WorkDirectory = Path.Combine(UpipeServer.ProjectPath, "Temp", "UpipeEval");
        }

        // Returns the assembly and its portable PDB, or the compile errors (assembly null). Throws CommandFailure
        // when the compiler itself fails. `noWarn` lists warning numbers that the generated wrapper code triggers.
        // `defines` are the preprocessor symbols the code is compiled with.
        public static (byte[] assembly, byte[] symbols, List<Error> errors) Compile(string name, string source, string noWarn,
            IEnumerable<string> defines = null)
        {
            Directory.CreateDirectory(WorkDirectory);
            var basePath = Path.Combine(WorkDirectory, name);
            var files = new[] { ".cs", ".rsp", ".dll", ".pdb" }.Select(extension => basePath + extension).ToArray();
            try
            {
                File.WriteAllText(files[0], source);
                var arguments = new StringBuilder();
                arguments.AppendLine("-nologo").AppendLine("-noconfig").AppendLine("-nostdlib+").AppendLine("-target:library")
                    .AppendLine("-langversion:9.0").AppendLine("-debug:portable").AppendLine("-optimize-")
                    // Diagnostics in English whatever the machine's language, as the official package reports them.
                    .AppendLine("-preferreduilang:en-US")
                    .AppendLine("-nowarn:" + noWarn);
                var symbols = (defines ?? Enumerable.Empty<string>()).Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
                if (symbols.Count > 0)
                    arguments.AppendLine("-define:" + string.Join(";", symbols));
                arguments
                    .AppendLine($"-out:\"{files[2]}\"");
                foreach (var reference in References())
                    arguments.AppendLine($"-r:\"{reference}\"");
                arguments.AppendLine($"\"{files[0]}\"");
                File.WriteAllText(files[1], arguments.ToString());

                var (exitCode, output) = Run(files[1]);
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
                if (exitCode != 0 || !File.Exists(files[2]))
                    throw new CommandFailure("Command Execution Failed", "EVAL_INTERNAL_ERROR: the compiler failed: " + output.Trim());
                return (File.ReadAllBytes(files[2]), File.Exists(files[3]) ? File.ReadAllBytes(files[3]) : null, errors);
            }
            finally
            {
                foreach (var path in files)
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

        static (int exitCode, string output) Run(string responseFile)
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
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(CompileTimeoutMilliseconds))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception)
                    {
                        // already exited
                    }
                    throw new CommandFailure("Command Execution Failed", "EVAL_INTERNAL_ERROR: the compiler did not finish in time.");
                }
                return (process.ExitCode, stdout.Result + stderr.Result);
            }
        }

        // Every assembly loaded in the Editor, so compiled code can use anything project code can; one per simple
        // name, because the compiler refuses two assemblies with the same name. Assemblies loaded from memory
        // (earlier evals) have no location and are left out.
        static IEnumerable<string> References()
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
