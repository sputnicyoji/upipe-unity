# Changelog

All notable changes to this package are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.3.3] - 2026-11-26

### Added
- Bundled companion agent skill under `Skills~/upipe-unity/SKILL.md` (in Unity's `~` hidden folder so no `.meta` or compile overhead is incurred) covering standalone `unity` CLI setup on Unity 2022.3 without Unity 6, migrating from third-party Unity MCP tools, verification loops, and Upipe-specific flags and gotchas.

### Changed
- Updated `README.md` with standalone `unity` CLI install steps, a `Using with AI Agents (CLI & MCP)` section (`unity command`, `unity mcp configure`, bundled skill copy instructions, and a third-party MCP → Upipe command mapping table), confirmed working Unity 2022.3 versions (`2022.3.62f3c1` and `2022.3.62f2c1` with `test-framework` `1.4.3` and `1.1.33`), and clickable links to `CHANGELOG.md` and `LICENSE.md`.

## [0.3.2] - 2026-11-25

### Added
- Two-tier SHA-256 compilation cache for `script eval` and `script run` in `CscCompiler`: an in-memory 64-entry LRU cache plus a bounded 128-entry local disk cache under `Temp/UpipeEval/cache/` keyed by compiler version, normalized source paths/contents, `-nowarn`, sorted `-define` symbols, and a reference assembly fingerprint `(path, length, lastWriteTimeUtcTicks)`.
- Deterministic namespace (`UpipeEval_<hash>`) and script assembly names (`UpipeRun_<hash>`) plus bounded void-trailing hash memoization in `ScriptEvalCommand` so repeated identical `script eval` snippets skip both the `CS0030` probe and out-of-process `csc.dll` recompilation while still calling `Assembly.Load(bytes, symbols)` per invocation with zero static-state leakage or in-process Roslyn memory bloat.

### Fixed
- `UpipeServer.ProjectPath` is canonicalized on Windows (`GetLongPathNameW`) to match `ResolveProjectPath`, preventing Windows 8.3 short-path mismatches in asset-relative screenshot imports and stack-trace stripping; `s_Port` is now `volatile` for lock-free HTTP thread reads.
- `DescriptorFile` checks `RuntimeInformation.IsOSPlatform(OSPlatform.Windows)` instead of `UnityEngine.Application.platform` so 15 s background timer heartbeats avoid Unity API calls off the main thread.
- `CscCompiler` L2 disk cache now serializes writes/pruning under `s_DiskCacheLock`, commits `.pdb` before `.dll` atomically (cleaning up any orphan `.pdb` on failed `.dll` writes or during `PruneDiskCache`, while eviction deletes an entry's `.pdb` only once its `.dll` is gone, so an entry whose `.dll` a reader still holds open on Windows keeps its symbols), and recovers valid cached `.dll` assemblies even when no `.pdb` is present.
- `ScriptEvalCommand.CompileScript` checks `CscCompiler.TryCompileFromCache` for the `wrapTrailing: false` form before spawning `csc.dll` on the `CS0030` probe, so cached void-trailing `script eval` snippets hit L2 directly across domain reloads.
- `TestCommands` now computes `missingTestNames` and includes `Missing test name(s) (TEST_NAMES_NOT_RUN)` details when a test run ends in `TEST_RUN_ERROR` after its last phase's `RunFinished` (test-framework 1.4.3 reports a post-build cleanup error that way, with every result in). An error raised before any test ran, such as one logged by an `IPrebuildSetup`, still lists no names.

## [0.3.1] - 2026-11-24

### Fixed
- Register `http://localhost:<port>/` directly on Mono's IPv4 `EndPointListener` (`127.0.0.1:<port>`) and accept `localhost:<port>` in `IsAllowedHost`: on Windows Mono, `http://localhost:<port>/` in `HttpListener.Prefixes` binds only `::1`, so when `unity recompile` (`1.0.0-beta.13`) switches `Host` from `127.0.0.1:<port>` to `localhost:<port>` after `ECONNREFUSED` during a multi-second domain reload, Mono's `HttpConnection` used to reject it with a raw non-JSON `400 Bad Request (Invalid host)`.
- Clear `HANDLE_FLAG_INHERIT` (`SetHandleInformation`) on Mono's listening socket right after `HttpListener.Start()`, abort the listener after draining replies (`listener.Abort()`), and set `KeepAlive = false` unconditionally on responses so child processes spawned with `UseShellExecute = false` (e.g. hot-reload compiler daemons on Windows) cannot keep Upipe's port bound across domain reloads.
- `StopFailureReply` serves the current `RecompileCommands.Current` snapshot with HTTP `200` for `script recompile_status` while `domainReloadInProgress` is set, and `RecompileCommands.Attach()` defers transitioning from `"compiling"` to `"completed"` until `MainThreadQueue.Settled` is true so an immediate follow-up `unity recompile` never hits `503 Server Busy (settling)`.

## [0.3.0] - 2026-11-21

### Added
- `script run --extra-files`: more `.cs` files or directories (every non-hidden `.cs` file below, recursively) compiled together with the main file. Values separated by `;` or `,`, a JSON array, a structured string or array (`extra-files` / `extra_files`), or the flag repeated in a raw argv request; paths are deduplicated the way the platform's file system compares them, the main file included.
- `script run` default entry: with extra files, `Run` / `Main` is looked for only in the main file's own types; `--entry Type.Method` reaches any compiled file.
- `script run` instance entries: without a static candidate, an instance `Run` / `Main` (or the `--entry` method) runs on a new instance of a non-abstract, non-generic class with a parameterless constructor that is not a `UnityEngine.Object`, so legacy entry classes with `public void Main()` run unchanged. Other classes are refused with `INVALID_COMMAND_ARGS` and the reason.
- `test run --test-names`: exact full names (a fixture, parameterized method or namespace name runs everything under it), separated by `;` or by commas outside parentheses and quotes, or a JSON array; not together with `filter`. Once the whole run (both halves of `mode all`) is over, every requested name must have hit at least one result, otherwise `test run` and `test status` fail with `TEST_NAMES_NOT_RUN` listing the missed names. `test status` reports `testNames` and `missingTestNames`.
- Job progress: `GET /api/job` (and `unity job status`) reports `progress: { message, percent }` while a job runs and never after it ends. `script run` passes progress parameters of standard library types (`Action<string, int>`, `Action<string>`, `IProgress<string>`, `IProgress<int>`, `IProgress<(string, int)>`) wired to the job, or no-ops outside one; a detached synchronous `test run` reports finished/total tests.
- `capture scene_view` (alias `capture_scene_view`): the Scene view's camera rendered off-screen (default) or the Scene view window as drawn (`source screen`); `NO_SCENE_VIEW` when there is none.
- `width` / `height` (1 to 4096, default 1280x720) for camera captures of `capture game_view` and `capture scene_view`.
- `capture game_view --source screen` reads any Play Mode view, the Device Simulator included.
- `editor quit` (alias `editor_quit`): answers, then quits from the next Editor update (a quit answered right before a domain reload still happens after it). An interactive Editor refuses with `PLAY_MODE_ACTIVE` or `UNSAVED_SCENES` unless `force` (or `confirm`) is set; `exit-code` sets the process exit code.
- `Tests~/smoke.py`: cross-platform smoke test in Python 3's standard library (Windows and macOS), at full check-for-check parity with `Tests~/smoke.sh` including `--quit` and `--startup`.
- `Tests~/HostFixtures`: test fixtures to copy into a host project's `Assets/` so `Tests~/smoke.sh` can check test names, test progress and `mode all`.
- `test cancel` cancels the run started here when the project has `com.unity.test-framework` 1.4.0 or newer (a dependency can pull it above the 1.1.33 that 2022.3 ships): through `TestRunnerApi.CancelTestRun` with the guid each phase was started with (kept across domain reloads), or, for a PlayMode run already in Play Mode, by leaving Play Mode (a canceled run that gets into Play Mode anyway, or whose framework job outlives the cancel, is stopped again). The run ends with the new status `canceled` (`completed` if the run had reported its end before the cancel), and the scenes are restored as the Test Framework would at the end of a run (the saved scenes it started from, or a new scene). The framework's EditMode runner, which its cancel leaves counting as running (an `Unexpected assembly reload happened while running tests` error at the next domain reload), is released; the other steps the framework skips on a cancel (`[PostBuildCleanup]`, undo, the running test's teardown) are not redone. `test cancel` answers the run's summary once it stops, or `TEST_CANCEL_FAILED` when the framework has no running job for it; `test status` and a waiting `test run` fail with `TEST_RUN_CANCELED`, and a detached `test run` job ends `failed`. A canceled `mode all` does not start its PlayMode half. `Tests~/HostFixtures` gains a slow PlayMode fixture for it.

### Changed
- With an older `com.unity.test-framework`, `test cancel` says canceling needs 1.4.0 or newer instead of claiming there is no API for it. `TEST_RUN_IN_PROGRESS` no longer says the run cannot be canceled; on 1.4.0 or newer it points to `test cancel` when the run is Upipe's.
- `CscCompiler` caches the referenced assembly list per domain and rebuilds it only after an assembly loads from disk (in-memory eval and script run assemblies never invalidate it).
- `script run` compiles every file under `#line 1 "<its path>"`: diagnostics in extra files start with the file's name, and runtime stack traces name the real files.
- The fallback version (used when the package is not resolved by the Package Manager, for example when the Editor folder is mounted under `Assets/`) follows `package.json`.
- Structured requests are bound by the same rules as argv: a boolean or integer parameter takes only a value that parses as one (otherwise `INVALID_COMMAND_ARGS` with `typeMismatch`, where the handler used to fail with `Parameter Validation Failed` or read the value as false), and a JSON `null` counts as not given, so a required parameter sent as `null` is `missingRequired`.
- `editor playmode start` (`editor_play`) refuses with `SCRIPT_COMPILATION_FAILED` while scripts have compile errors and no compile is running: Unity would not enter Play Mode, and the Run In Background override would stay on.
- A request timeout or a job cancel stops the out-of-process compiler of `script eval` / `script run` instead of waiting for it (up to 120 s).
- Stopping the server by hand ends a test run that is still going as `interrupted` (its results after the stop would be lost), or as `completed` when every result is already in.
- A run the Test Framework ends in an error (`TEST_RUN_ERROR`) also reports how many tests passed and the last error logs of the run (`errorLog` in `test status`). test-framework 1.4 hides the cause of such an error behind a fixed "Multiple unexpected errors happened while running tests."
- `Tests~/smoke.sh` and `Tests~/smoke.py` ask the server whether test cancel is compiled in instead of reading `packages-lock.json`, check that a 413 reply closes the connection, and check that a canceled `mode all` run never starts its PlayMode half, that a synchronous run cut off by a domain reload is not resent, and (1.4+) that a run error reports the passes and the logged errors. `Tests~/HostFixtures` gains the fixtures these use; they run only when named.

### Fixed
- Windows: the descriptor file is owner-only (a protected DACL with one entry for the current user). It used to inherit
  read access for Users / Authenticated Users in projects outside the user profile, which exposed the token. It is now
  swapped in with `MoveFileEx`, which keeps the new file's DACL; `File.Replace` kept the replaced file's.
- Windows: after a domain reload the server waits up to 2 s for its previous port, which the old domain's socket holds
  for up to ~200 ms. Moving to another port stranded a CLI polling the old one (`unity recompile` timed out).
- `console get` stopped capturing after the Editor left Play Mode; the log handler is subscribed again on entering Edit and Play Mode.
- A `script eval` / `script run` cut off by a domain reload while it was awaiting answered the retryable 503 `domain_reload`, so the CLI resent it and the script ran again from the top; it now fails with a non-retryable error saying the script had started.
- `script eval` with `"dry-run": "1"` (or `1`) in a structured request compiled and ran the code.
- `script eval` snippets: a `using` or type declaration inside `#if` ... `#endif` failed with CS1028 and a moved type lost its condition; a qualified object initializer as the last expression (`new UnityEngine.Rect { x = 1 }`), a bare `return;` in a `case x: { }` block or after `catch (...) when (...)`, an anonymous `delegate { }` passed to a call, and a statement right after a type declaration were misread.
- A command waiting for the main thread when the server was stopped answered an internal error instead of `Server Stopped`.
- Turning the server off and on again could bring back a test run or job table saved before an earlier domain reload.
- `test run --mode all` started its PlayMode half even when the EditMode half had left scenes modified, which could stop the Editor at a save dialog; it now ends with `UNSAVED_SCENES`.
- A synchronous `test run` could answer with another run that started right after its own one ended.
- A script's cancellation callback that throws no longer aborts a job cancel, a request timeout or a server stop.
- Camera captures leaked a temporary RenderTexture when rendering threw.
- An unreadable `UserSettings/UpipeSettings.json` broke the server's static constructor; it now keeps the default (enabled).
- `/api/status` reported a fresh `lastHeartbeat` even when the instance descriptor could not be written.
- Turning the server off and on from the menu dropped an `editor set_auto_tick --persist` interval.
- A failed or cancelled Player build left a stale paused Run In Background value that a later override could reapply.
- In an interactive Editor a `test run` started right after another one ended was refused with `TEST_RUN_IN_PROGRESS`, because the Test Framework keeps its job marked running for a few Editor updates after the run ends; a run now reads as completed only once the framework has let go of its job (at most 10 seconds).
- Synchronous `test run` waiting across a domain reload or manual server stop could race `shutdown` cancellation and reply with retryable `503 domain_reload`; it now surfaces the completed `interrupted` run directly.
- Stopping or reloading the server clears `MainThreadQueue.Settled`, rejects new job submissions after `CancelAll`, and cancels active detached job tokens on `SaveForReload()` so child compiler processes exit promptly.
- `MainThreadQueue.Run` / `RunAndWait` now honor `CancellationToken` while queued or waiting on an unpumped main thread; `CscCompiler.GetReferences()` no longer caches a computed reference list after `Detach()`.
- `ConsoleCommands` forgets persisted buffers on manual stop, avoids false `dropped` reports after reloading an empty or freshly cleared buffer, and keeps `AutoTick` / `RecompileCommands` state consistent across same-domain server toggles.
- `EvalSource` handles generic local functions (with or without `where` constraints) and C# 8/9 property patterns (`is { ... }`, `is not { ... }`, typed patterns), `ScriptEvalCommand.ToJson` preserves `JToken` / `JObject` values, and `ScriptRunCommand.Await` retains exception diagnostics.
- `TestCommands.MatchesTestName` matches generic test methods/fixtures (`<`) and nested prefixes (`+`), `SplitTestNames` separates parentheses and angle-bracket depths cleanly, and `GrabWindow` no longer crops 1 px from zero-border edges.
- Windows: `script run` and `--extra-files` expand existing 8.3 short paths (such as `%TEMP%`) via `GetLongPathNameW` before compiling and matching diagnostics, keeping error formatting and extra-file deduplication aligned with Roslyn's `#line` expansion.
- `capture scene_view --source screen` picks the temporary render target `GUIView.GrabPixels` writes into by graphics API: Linear on D3D11, where an sRGB one washed the capture out in Linear color space, and sRGB elsewhere, which Metal needs to keep the capture from going dark.
- On `com.unity.test-framework` 1.4+, a run that ended with a run error (`TEST_RUN_ERROR`) left the Editor on the test's untitled scene: the framework skips its scene restore after an error, as it does on a cancel. Upipe now reopens the scenes the run started from, as after a cancel.
- `413 Payload Too Large` responses close the connection (`KeepAlive = false`), and `Tests~/smoke.py` polls for an early 413 reply before streaming body chunks to avoid Windows TCP reset discarding the response.

## [0.2.0] - 2026-11-20

### Added
- On-demand `script recompile` (`--force` flag optional, default `false`): skips `CompilationPipeline.RequestScriptCompilation()` and returns `up_to_date` when `AssetDatabase.Refresh()` finds no script/assembly/manifest changes and no prior compilation errors exist.
- New-domain readiness gating: `MainThreadQueue.Settled` stays `false` (`503 settling`) across warm domain reloads and Play Mode transitions until the new domain runs an idle `EditorApplication.update` tick, and `/api/status` / `editor status` now report `lastTickAgeMs`.
- `console_write` and `editor_pause` command aliases so `unity mcp` exposes unique prefixed tool names instead of bare verbs, while keeping unique bare short names working for CLI/structured calls.
- `"tests.camelCase"` capability advertised when `com.unity.test-framework` is present.
- `RunInBackgroundBuildGuard` (`IPreprocessBuildWithReport` / `IPostprocessBuildWithReport`) to temporarily restore the project's saved `Application.runInBackground` around Player builds when overridden by a Play Mode session.
- Detached job result spilling (`> 32 KB`) to `<Project>/Temp/UpipeJobs/<jobId>.json` across domain reloads, plus automatic cleanup on job pruning.
- `Tests~/smoke.sh --startup` cold-start settling verification and extended end-to-end assertions for `mode=all` relay, `Assets/` capture import, aliases, UTF-8 / `#if UNITY_EDITOR` eval, and `lastTickAgeMs`.

### Changed
- Lazy-mounted runtime hooks (`MainThreadQueue`, `ConsoleCommands`, `RecompileCommands`, `AutoTick`, `PlayModeCommands`, `TestCommands`): disabled mode (`Tools/Upipe/Server Enabled` off or `UPIPE_ENABLE=0`) registers only in-memory commands with zero Editor update/log/compilation hooks attached.
- Separated `UpipeServer.Stop(forReload: true)` (retryable `503 domain_reload`) from manual/quitting stops (non-retryable `Server Stopped`, linked shutdown token cancellation, `JobManager.Live.CancelAll()`, restoring `runInBackground`, stopping `AutoTick`).
- `UpipeServer.Version` is now resolved from `PackageInfo.FindForAssembly` instead of a separate constant.
- `CscCompiler` configures `-utf8output`, `-codepage:65001`, no-BOM UTF-8 writes, and shares `EditorDefines` with both `script eval` and `script run`.
- Replaced `EditorApplication.delayCall` usage in `TestCommands` with `EditorApplication.update` and an idle fallback when reflecting newer `test-framework` internals returns `null`; removed hardcoded `1.1.33` strings from user-facing messages.

### Fixed
- Widened `UpipeServer.Stop()` in-flight HTTP draining to cover the entire request/response `Send()` path, ensured `HttpListenerResponse.Close()`, and suppressed console exception noise on client disconnects.
- Rejected late `MainThreadQueue.Run()` enqueues once `DomainReloadInProgress` or server shutdown starts.
- Fixed cross-thread snapshot/timestamp tearing in `ConsoleCommands` ground truth and wrapped `UnityEditor.LogEntries.Clear` reflection invocation in `try/catch`.
- Fixed `RecompileCommands.Watchdog()` start-grace tracking after slow imports and post-csc compilation failure reporting via `FailedBeforeStart`.
- Disposed `JobManager` timeout `CancellationTokenSource` timers immediately upon completion, capped active detached `Upipe.Job` threads, and propagated `OperationCanceledException` from `Wait` cancellation in `ScriptEvalCommand` / `ScriptRunCommand`.
- Fixed `TestRunnerApi` native `ScriptableObject` retention across reloads/stops by destroying `s_Api` in `TestCommands.Detach()`.
- Fixed case-insensitive `Assets/` path recognition in `CaptureCommands.Save()` on Windows and macOS.
- Added transient `IOException` retries to `DescriptorFile.Write()` and `!process.HasExited` verification in `DescriptorFile.IsRunning()`, and reduced `Publish()` lock contention.

## [0.1.0] - 2026-11-19

### Added
- Initial Unity 2022.3 Editor HTTP server package (`com.upipe.server`) compatible with the official `unity` CLI (`unity command ...`, `unity recompile`, `unity list`, `unity status`, `unity job`, and `unity mcp`).
- Commands for `editor status`, `editor set_auto_tick`, `editor.playmode start/stop/pause/set_run_in_background`, `script recompile/recompile_status`, `script eval`, `script run`, `console get/status/clear/write`, `test list/run/status/cancel`, and `capture game_view`.
