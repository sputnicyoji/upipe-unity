# Upipe Server (`com.upipe.server`)

Independent Editor HTTP server for Unity 2022.3 that the official `unity` CLI (`unity command ...`) can drive, the same way it drives a Unity 6 project with `com.unity.pipeline`. Implemented independently under the MIT license from public Pipeline documentation and observed CLI behavior; it contains no code from `com.unity.pipeline`. Not affiliated with or endorsed by Unity Technologies.

Version 0.3.4 for Unity 2022.3. Not supported: `commandLine` form, Pipeline binary transport.

## Highlights

- **Pure Editor assembly (`Upipe.Editor`)**: zero runtime code shipped in Player builds, lazy-attach when disabled (`UPIPE_ENABLE=0` or local switch) with zero runtime hooks or polling overhead.
- **Unfocused automation on Windows & macOS**: background-friendly command queue, `editor.playmode`, `test run` via `TestRunnerApi` (`EditorApplication.update`-driven), and `editor quit`.
- **Two-tier SHA-256 `script eval` cache**: 64-entry in-memory LRU + bounded 128-entry local disk cache under `Temp/UpipeEval/cache/` skips out-of-process `csc.dll` recompilation on cache hits while loading a fresh isolated assembly per invocation.
- **Complete test & capture workflow**: EditMode/PlayMode/`all` test execution, exact `--test-names`, `test cancel` on `com.unity.test-framework` 1.4.0+, off-screen & screen `capture game_view` / `capture scene_view` (including Device Simulator).
- **Hardened localhost server**: loopback-only listener (`127.0.0.1:<port>` + Windows IPv4 `localhost:<port>`), `Origin` rejection, 256-bit `SessionState` bearer token, owner-only descriptor permissions (`0600` / protected Windows DACL), and Windows 8.3 path canonicalization.

## Requirements

- **Unity Editor**: 2022.3 (verified on `2022.3.62f3c1` on macOS Metal and Windows 11 D3D11, and `2022.3.62f2c1` on Windows 11 D3D11)
- **Official CLI**: `unity` CLI (`1.0.0-beta.13`; works with `unity command ...`, `unity recompile`, `unity status`, `unity job ...`, and `unity mcp`)
- **Package dependency**: `com.unity.nuget.newtonsoft-json` `3.0.2` (declared in `package.json`)
- **Optional**: `com.unity.test-framework` (`1.1.0+` for `test list/run/status` and `tests.camelCase`; `1.4.0+` for `test cancel`)

## Install & Quick Start

The official `unity` CLI is a standalone binary that works independently of Unity 6. Only Unity's built-in `com.unity.pipeline` package requires Unity 6.0+, so installing `com.upipe.server` lets Unity 2022.3 projects use the exact same CLI and MCP tooling.

1. **Install the standalone `unity` CLI** (if `unity --version` is not yet installed; no Unity 6 Editor required):

   ```powershell
   # Windows (PowerShell)
   irm https://unity.com/install.ps1 | iex
   ```
   ```bash
   # macOS / Linux
   curl -fsSL https://unity.com/install.sh | bash
   ```

2. **Install `com.upipe.server` in your Unity 2022.3 project** (`unity pipeline install` rejects Unity < 6.0; do not install alongside `com.unity.pipeline`, as both write `Library/Pipeline/.unity-pipeline-port`):
   - **From Git** (recommended; append `#<tag>` to pin a release) in `Packages/manifest.json`:

     ```json
     "com.upipe.server": "https://github.com/sputnicyoji/upipe-unity.git#v0.3.4"
     ```

   - **Embedded**: copy this folder to `<project>/Packages/com.upipe.server/`. Unity picks it up on the next focus or launch. The server is on by default for everyone who opens the project; see [Local Switch](#local-switch) below.
   - **Local path** (developing the package itself) in `Packages/manifest.json`:

     ```json
     "com.upipe.server": "file:/absolute/path/to/Packages/com.upipe.server"
     ```

   - **Mounted under `Assets/`** (leaving tracked project files unchanged): copy `Editor/` and `Editor.meta` to `<project>/Assets/<folder>/` and list `/Assets/<folder>/` and `/Assets/<folder>.meta` (prefixed with the project's repository subpath when nested) in `.git/info/exclude`. Package Manager does not see an `Assets/` mount, so `editor status` reports the server's built-in fallback version, which is kept equal to `package.json`.

3. **Open the project and verify**: the console logs `[Upipe] HTTP server listening on 127.0.0.1:<port>`. Run:

   ```bash
   unity command --project-path <project> editor status
   ```

## Using with AI Agents (CLI & MCP)

- **Shell CLI first (recommended)**: agents running `unity command ...` use far less context than loading dozens of MCP tool schemas. Check readiness with `unity status --format json` and discover live schemas with `unity command --format json --project-path <project>`.
- **MCP mode (`unity mcp`)**: if your AI client expects MCP tools instead of shell commands, the official CLI serves Upipe's 21 commands over stdio MCP without a third-party MCP server:

  ```bash
  unity mcp configure <client> --project-path <project>   # see `unity mcp configure --list`
  ```

- **Install the bundled agent skill**: copy [`Skills~/upipe-unity/`](Skills~/upipe-unity/SKILL.md) into your client's skills folder (`.claude/skills/upipe-unity/`, `.cursor/skills/upipe-unity/`, or `.agents/skills/upipe-unity/`) so agents know not to run `unity pipeline install` on 2022.3 and follow Upipe's verification loops. Optionally install Unity's general CLI skill with `unity skill install <client>`.
- **Switching from third-party Unity MCP servers** (no `manage_scene` / `manage_gameobject` / `manage_asset` needed):

  | Workflow | Upipe + `unity` CLI |
  |---|---|
  | Scene, hierarchy, prefab, component, or asset edits | `unity command eval '...'` or `unity command script run -- --file <path>` |
  | Refresh assets & check compile errors | `unity recompile --strict --project-path <project>` |
  | Inspect or clear Editor logs | `unity command console get -- --tail 50 --level error` |
  | Run EditMode / PlayMode tests | `unity command test run -- --test-names Ns.Fixture` |
  | Capture Game or Scene view PNG | `unity command capture game_view -- --save-path Temp/shot.png` (or `capture scene_view`) |

## Supported Commands

| Command | Notes |
|---|---|
| `editor status` | Ready/settling, compiling, play mode, `autoRefresh`, `runInBackground`, `domainReloadInProgress`, `lastTickAgeMs` (ms since the current domain's last main-thread tick, or `null` before the first tick), and in play mode `frameCount` and `playerLoopTicking` (also on `unity status`). Answered off the main thread; also served as `GET /api/editor_status`. |
| `script recompile` | Refreshes assets (`AssetDatabase.Refresh()`); requests compilation when script/assembly/manifest files changed, a previous compile failed, or `force=true` (`false` default), otherwise returns `up_to_date` without a domain reload. `focus` is accepted and ignored. |
| `script recompile_status` | `{ status, failed, errors, compilationFailed }`; errors are `file(line,col): error CSxxxx: text` lines. |
| `console get` | Log buffer with `tail`, `level`, and a `since` + `since-session` cursor. Holds compiler warnings and errors, survives domain reloads, starts over with each Editor session. |
| `console status` | Cursor, retained counts and Editor ground truth, without entries. |
| `console clear` | Clears the Editor console and the captured entries. Cursors stay valid; cleared entries never count as `dropped`. |
| `console write` | Alias `console_write`. `message`, `level` log \| warn \| error (default log). |
| `editor.playmode start` | Enters Play Mode; `run-in-background` (default true) turns Run In Background on until Play Mode ends. Returns before the domain reload, so follow it with `unity status --until-ready`. Refused with `SCRIPT_COMPILATION_FAILED` while scripts have compile errors and no compile is running. |
| `editor.playmode stop` | Exits Play Mode. |
| `editor.playmode pause` | Alias `editor_pause`. `paused` (default true); `false` resumes. |
| `editor.playmode set_run_in_background` | `enable` (default true) turns Run In Background on for this Editor session; `false` puts the project's value back (it does not force false). |
| `editor set_auto_tick` | `enable` (default true), `interval-ms` (default 16), `persist` (default true: survives domain reloads in this Editor session). See the note on auto tick below. |
| `script eval` | Compile and run C# with the Editor's active script compilation defines and UTF-8 encoding: statements, leading `using`s, local functions, types, `await`; `return x;` or a trailing expression is the result. `code` or `file`, `timeout` (s) or `timeout-ms`, `dry-run`. A script cut off by a domain reload after it started fails without the retryable 503, so the CLI does not run it again. Not supported: `auto-import`, `internals`, the `Wait` helper. |
| `test list` | `mode` all (default) \| editor \| playmode. One record per test: `FullName`, `Mode`, `Assembly`, `Categories`, `Explicit`. |
| `test run` | Runs tests in this Editor through `TestRunnerApi` (driven by `EditorApplication.update`, works while unfocused). `mode` editor (default) \| playmode \| all (EditMode, then PlayMode), `filter`, `filter-type` TestName (case-insensitive part of the full name, default) \| Assembly \| Category, `async-tests` (default false), `timeout` (s, default 600), `test-names` (exact full names, see below; not with `filter`). playmode and all need `async-tests`. Fails unless at least one test ran and none failed, and unless every name in `test-names` ran something; the failing tests and their messages are in `errorDetails`. Run with `--detach`, a synchronous run reports `finished/total tests` as the job's progress. |
| `test status` | The last run started here: `status` running \| completed \| canceled \| interrupted \| error (or `no_tests`), `testNames` and `missingTestNames` when names were given, `errorLog` after an `error`, `summary`, `results` (`fullName`, `status`, `duration`, `message`, `stackTrace`). `detail` full (default) \| failures \| summary. A finished run that failed answers as a failure, like `test run`. Answered off the main thread; also served as `GET /api/test-status?detail=`. |
| `test cancel` | Alias `test_cancel`. With `com.unity.test-framework` 1.4.0 or newer, cancels the run started here and answers its `test status` summary once it stops (see below). Older versions answer `NOT_SUPPORTED` while a run is going. `{"status":"no_tests"}` when no run is going. |
| `capture game_view` | PNG of the Game view. `source` screen (default: what a Play Mode view drew, the Game view or the Device Simulator, overlay UI included; the focused one first) \| camera (`Camera.main`, else the first enabled camera, off-screen at `width` x `height`, 1 to 4096, default 1280x720; the sizes are checked whatever the source), `save-path` or `output-path` (project-relative or absolute; files under `Assets/` are imported with case-insensitive matching on Windows/macOS), `include-inline-image`, `max-resolution` (longest edge of the inline copy). Not supported: `camera`, `overlay`. With a path the reply is `savedPath`, `width`, `height`, `bytes` only; without one it carries the image as `base64` (`encoding: png`), which `unity mcp` passes on as an image. Fails with `NO_GPU` under `-nographics`. |
| `capture scene_view` | Alias `capture_scene_view`. PNG of the Scene view, same parameters and reply as `capture game_view`. `source` camera (default: the Scene view's camera rendered off-screen at `width` x `height`, without grid or gizmos) \| screen (the Scene view window as drawn, gizmos and overlays included, at its own size). `NO_SCENE_VIEW` when no Scene view is open (batchmode) or, for screen, when it is not the visible tab. |
| `editor quit` | Alias `editor_quit`. Answers `{"quitting": true, "exitCode": N}`, then quits from the next Editor update once the reply is out (works while unfocused). An interactive Editor refuses with `PLAY_MODE_ACTIVE` or `UNSAVED_SCENES` unless `force` (or `confirm`) is set; a batchmode Editor always quits. `exit-code` (default 0). `unity close <project>` does not use it: it sends an `eval` of `EditorApplication.Exit(0)`, which works too but checks nothing. |
| `script run` | Compile a `.cs` file (plus `extra-files`) from anywhere on disk and call one of its methods; no asset import, no domain reload. `file`, `entry` (`Type.Method` in any compiled file; default: the main file's only `Run` or `Main`), `args` (JSON), `defines`, `timeout-ms`, `extra-files` (files or directories, see below). Awaits a returned `Task`, `Task<T>`, `ValueTask`, `UniTask` or any other awaitable by its shape. The parameter names are this package's own: no public document lists the official ones. |

## Behavior & Notes

### `script eval`

`script eval` compiles with the Roslyn compiler that ships with the Editor (`DotNetSdkRoslyn/csc.dll`, run by `NetCoreRuntime/dotnet`), backed by a two-tier SHA-256 compilation cache (64-entry in-memory LRU plus a 128-entry local disk cache under `Temp/UpipeEval/cache/`): cache hits skip out-of-process `csc.dll` recompilation while still loading a fresh isolated assembly (`Assembly.Load(bytes, symbols)`) per run. Each run loads one small assembly that cannot be unloaded until the next domain reload. Like the official command it runs arbitrary code in the Editor; the bearer token is the only gate.

How a snippet is read (`Editor/EvalSource.cs`): `using` directives move to the top, type declarations (including `record`, `delegate` and extension-method classes) move into the snippet's own namespace, the rest runs as an async method, and a trailing expression is the result (a trailing `void` call returns null). `Object` and `Random` mean the UnityEngine ones unless the script aliases them. Results: Unity objects and Unity value types as their `ToString()`, collections and plain objects as JSON, framework types (`Task`, `Type`, ...) as their `ToString()` so that nothing blocks reading them. Diagnostics read `line L, col C: error ID: message` with a 0-based column, as the official package prints them.

### `script run`

`script run` compiles with the same compiler (`-utf8output`, `-codepage:65001`), with the Editor's script defines plus `defines`. One bindable parameter takes the whole `args` value; more take a JSON array (by position) or object (by name). A `CancellationToken` parameter gets the run's token, which the request timeout or a job cancel cancels. Progress parameters of standard library types (`Action<string, int>`, `Action<string>`, `IProgress<string>`, `IProgress<int>`, `IProgress<(string, int)>`) are passed too, wired to the job's progress under `--detach` and no-ops otherwise, so a script reports progress without referencing this package. Static methods are looked for first; without one, an instance method runs on a new instance of a class that is not abstract, not generic, not a `UnityEngine.Object` and has a parameterless constructor (others are refused with the reason). `extra-files` takes `.cs` files and directories (every non-hidden `.cs` file below them), separated by `;` or `,` or as a JSON array; the CLI refuses a repeated flag, while raw argv requests may repeat it. Every file compiles under `#line 1 "<its path>"`: errors in an extra file start with its name and stack traces name the real files.

### Detached jobs

`unity command <command> --detach` submits any command as a job and prints its id; `unity job status`, `unity job wait` and `unity job cancel` follow it (`GET /api/job?id=`, `POST /api/job/cancel`). States are `queued`, `running`, `completed`, `failed` and `canceled`. At most 16 jobs can be unfinished and at most 16 background worker threads active (429 past either); the last 100 finished jobs are kept for an hour. A job is bounded only by the command's own `timeout-ms` (ms) or `timeout` (s) parameter (`timeout-ms` takes precedence if both are given), because the CLI sends no usable request timeout with `--detach`. Cancelling a queued job means it never runs; a running one gets its token cancelled and ends `canceled` if it stops. The job table survives domain reloads (results larger than 32 KB spill to `Temp/UpipeJobs/`), and a job a reload cut short reads as `failed` (`Job Interrupted`). While a job runs, its status may carry `progress` (`message`, `percent` 0 to 100 or null), which `unity job status` shows; a finished job never does.

### Readiness settling

On initial session cold start the server stays `settling` (`503` on `/api/commands`) for 1.0 second of continuous idle; after subsequent domain reloads or Play Mode transitions the new domain stays `settling` until its main thread completes an idle `EditorApplication.update` tick (`lastTickAgeMs` is `null` until that first tick).

### Tests & `test cancel`

Tests run inside the Editor that serves the request, so there is no second Editor and no `-runTests` exit problem. A synchronous `test run` waits off the main thread while the Test Framework runs; the CLI's 30 s request timeout applies, so pass `unity command --timeout <s>` for longer suites, or `--async-tests true` and poll `test status`. Entering Play Mode reloads the domain: the run, with every result so far, is saved to `SessionState` before each reload and the callbacks are registered again afterwards, which is why PlayMode runs need `async-tests`. A synchronous run that a reload cuts off reads as `interrupted`. When the Test Framework ends a run with an error of its own (`TEST_RUN_ERROR`; 1.4 does so for an error logged while the run cleans up, with the fixed message "Multiple unexpected errors happened while running tests."), the failure also says how many tests passed, lists `Missing test name(s) (TEST_NAMES_NOT_RUN)` / `missingTestNames` when the error occurs after the last phase's `RunFinished`, and includes the last error logs of the run, which `test status` keeps as `errorLog`. The Test Framework skips its scene restore after such an error, as it does on a cancel, so Upipe reopens the scenes the run started from (see below). While a PlayMode run enters Play Mode the server is briefly unreachable, so a script polling `unity command test status` should read the message rather than stop at the first non-zero exit (a finished run that failed also exits non-zero). A run is refused while another one is going, while the Editor is in Play Mode, and in an interactive Editor while a loaded scene has unsaved changes (the Test Framework would stop at a save dialog).

`test cancel` needs `TestRunnerApi.CancelTestRun`, which `com.unity.test-framework` has from 1.4.0. An EditMode run (or a PlayMode run that is not in Play Mode yet) stops through it. A PlayMode run in Play Mode stops by leaving Play Mode, because the Test Framework would let its tests go on to the end; a canceled run that reaches Play Mode anyway leaves it again. The run ends `canceled` with the results of the tests that finished, or `completed` when the run had reported its end before the cancel came (a cancel that wins that race ends it `canceled`, results complete). The Test Framework skips its scene restore on a cancel (and after a run error it reports), so Upipe does it: the saved scenes the run started from are reopened, or a new scene replaces the test scene when it started from an untitled one (scenes inside an immutable package, or a setup one of whose scenes was deleted, are not reopened). On a cancel the framework also skips `[PostBuildCleanup]`, the undo of the tests' changes, its cleanup checks and the teardown of the test that was going; Upipe does not redo those. It does release the framework's EditMode runner, which the cancel leaves counting as running: left alone, it logs `Unexpected assembly reload happened while running tests` at the next domain reload (the Test Runner window's own cancel leaves it the same way). `test cancel` waits up to 10 s for the run to stop (a domain reload into Play Mode ends the wait early) and otherwise answers it as `running` with `cancelRequested: true`. `TEST_CANCEL_FAILED` means the Test Framework had no running job for the run, which may be ending on its own; poll `test status`. A synchronous `test run` waiting on the canceled run and `test status` then fail with `TEST_RUN_CANCELED`, and a detached `test run` job ends `failed`. `unity job cancel` on such a job stops only the waiting, not the run.

`test-names` are exact full names separated by `;` (or by commas outside parentheses and quotes, so `Ns.Fixture.Add(1,2)` and `Ns.Fixture.Quote("a,b")` stay whole), or a JSON array. A fixture, parameterized method or namespace name runs everything under it. Once the whole run is over (both halves of `all`), each requested name must have hit a result; any that did not fail the run with `TEST_NAMES_NOT_RUN` and are listed, so a misspelled name never reads as a pass. The commands exist and `"tests.camelCase"` is advertised in `capabilities` when the project has `com.unity.test-framework` 1.1.0 or newer; without it the package compiles without them (`test ...` is then an unknown command) and leaves its own test assembly out.

### Recompile, Run In Background & Auto tick

`unity recompile` works end to end, including `--strict`, which fails on warnings produced by the compile.

Run In Background is a Player Setting that ships in builds. Before the first override in an Editor session the server records the project's value in `SessionState`, puts it back when the Editor returns to Edit Mode or quits, and temporarily restores the project's value around Player builds via `IPreprocessBuildWithReport` / `IPostprocessBuildWithReport`, so `ProjectSettings/ProjectSettings.asset` and Player builds never keep the override.

Auto tick: while the Editor is unfocused, a timer posts a tick to the main thread that drains queued commands and calls `EditorApplication.QueuePlayerLoopUpdate()`. Measured on macOS with 2022.3.62 it changes nothing you can see: posts run on the Editor's own unfocused tick (about 10 per second), so queued commands still wait about 65 ms, and a game in Play Mode with Run In Background off stays at frame 1 while the Editor is unfocused, auto tick or not. What keeps an unfocused game advancing is Run In Background, which `editor.playmode start` turns on by default. The command exists because the CLI's play-mode recipe calls it.

### CLI compatibility & timeouts

CLI compatibility: the `unity` CLI 1.0.0-beta.13 and its skill docs use single-token names the public Pipeline docs do not define. They work in argv and structured requests: `editor_status`, `console` (for `console get`; a bare `unity command console` still lists the group), `console_status`, `console_write`, `clear_console`, `eval`, `recompile`, `recompile_status`, `editor_play`, `editor_stop`, `editor_pause`, `editor_set_run_in_background`, `set_autotick`, `script_run`, `run_tests`, `test_status`, `test_list`, `test_cancel`, `capture_game_view`, `capture_scene_view`, `editor_quit`, plus any unambiguous `<group>_<name>`. Underscore parameter spellings (`since_session`, `timeout_ms`) bind to the hyphenated parameter. Structured values follow argv's rules: a boolean or integer parameter takes only a value that parses as one, and `null` counts as not given. The single-token name is also the catalog `name`, which `unity mcp` uses as the MCP tool name, so it is unique. The official package rejects `console_status`, so its `--strict` cannot read the console.

The CLI always sends a request timeout (30000 ms by default, `--timeout` seconds times 1000, 60000 ms from `unity mcp`), and it wins over a command's own `timeout` (s) and `timeout-ms` parameters. So the skill docs' `eval -- --timeout 15000` (meant as ms) is ignored through the CLI and the 30000 ms request timeout applies; the parameters only decide for clients that send no request timeout, where `timeout` is read as seconds.

## Local Switch

- `Tools/Upipe/Server Enabled` starts or stops the server in the running Editor. Off stops background Editor hooks, cancels active jobs, restores `Run In Background` / auto tick, removes the descriptor and frees the port; starting disabled attaches only in-memory commands with zero runtime hooks.
- The choice is saved in `<project>/UserSettings/UpipeSettings.json` (`{"enabled": false}`), which projects do not commit. No file means enabled.
- `UPIPE_ENABLE=1|true|0|false` in the Editor's environment overrides the file for that process and is never written back; the menu item is greyed out while it is set.

## Testing

- Unit tests: add `"testables": ["com.upipe.server"]` to the project manifest and run the EditMode tests, for example in a running Editor: `unity command --timeout 600 run_tests -- --filter-type Assembly --filter Upipe.Editor.Tests`. Run that way, the tests that stop and restart the server skip themselves (stopping would cut off the CLI's connection), and the console tests clear that Editor's console.
- Smoke test against a running Editor: `Tests~/smoke.sh <project> [path-to-unity-cli]`. Against a freshly launched Editor: `Tests~/smoke.sh --startup <project>`. Against an Editor started with `UPIPE_ENABLE=0`: `Tests~/smoke.sh --disabled <project>`. Quitting the Editor through `editor quit`: `Tests~/smoke.sh --quit <project>`. Copy `Tests~/HostFixtures` into the project's `Assets/` to cover test names, test progress and `mode all`.
- `Tests~/smoke.py` matches `smoke.sh` check for check (including `--quit`) with Python 3's standard library only, and is the one to use on Windows: `python Tests~/smoke.py [--disabled|--startup|--quit] <project> [path-to-unity-cli]`. Without a CLI path it uses the install script's location (`~/.unity/bin/unity`, `%LOCALAPPDATA%\Unity\bin\unity.exe`), then `PATH`.

Known limits:
- Enabled by default: committing the package to a shared repository turns the server on for every teammate until they switch it off locally.
- 0.3.0 is verified on macOS (Metal) and Windows 11 (D3D11, 100% display scaling) with unity CLI 1.0.0-beta.13 and `com.unity.test-framework` 1.1.33 and 1.4.3: `-batchmode`, cold start, disabled mode, `--quit`, the interactive Editor and `unity mcp`. CLI 1.0.1 passed the 0.2.0 smoke test except `unity list`, which 1.0.1 removed. 0.3.2–0.3.4 are verified with unity CLI 1.0.0-beta.13 on `2022.3.62f3c1` (macOS and Windows 11, `com.unity.test-framework` 1.4.3) and `2022.3.62f2c1` (Windows 11, `com.unity.test-framework` 1.4.3 and 1.1.33); see [CHANGELOG.md](CHANGELOG.md).

Known issue: on 2022.3.62f3c1, `-batchmode -runTests` may not exit after writing results, with or without this package installed (always on macOS, now and then on Windows). Read the results file and stop the process.

## Security Model

Loopback only (`127.0.0.1`), `Host` pinned to `127.0.0.1:<port>` or `localhost:<port>`, requests carrying `Origin` refused, bearer token on every route. The token is 256 random bits kept in `SessionState` and published only in the descriptor file, which only its owner can read: mode 0600 on macOS/Linux, and on Windows a protected DACL with a single entry for the current user.

## Disclaimer & License

- **Independent implementation**: `com.upipe.server` is independently written from public Unity Pipeline documentation and observed CLI behavior; it contains no code from `com.unity.pipeline`. Not affiliated with or endorsed by Unity Technologies.
- **License**: MIT, see [LICENSE.md](LICENSE.md).
