# Upipe Server

Editor HTTP server for Unity 2022.3 that the official `unity` CLI (`unity command ...`) can drive,
the same way it drives a Unity 6 project with `com.unity.pipeline`. It contains no code from
`com.unity.pipeline`. Not affiliated with or endorsed by Unity Technologies.

Status: skeleton. No `commandLine` form, no Pipeline binary transport.

| Command | Notes |
|---|---|
| `editor status` | Ready/settling, compiling, play mode, `autoRefresh`, `runInBackground`, `domainReloadInProgress`, and in play mode `frameCount` and `playerLoopTicking` (also on the `unity status` row). Answered off the main thread; also served as `GET /api/editor_status`. |
| `script recompile` | Refreshes assets, then requests a compile. `focus` is accepted and ignored. |
| `script recompile_status` | `{ status, failed, errors, compilationFailed }`; errors are `file(line,col): error CSxxxx: text` lines. |
| `console get` | Log buffer with `tail`, `level`, and a `since` + `since-session` cursor. Holds compiler warnings and errors, survives domain reloads, starts over with each Editor session. |
| `console status` | Cursor, retained counts and Editor ground truth, without entries. |
| `console clear` | Clears the Editor console and the captured entries. Cursors stay valid; cleared entries never count as `dropped`. |
| `console write` | `message`, `level` log \| warn \| error (default log). |
| `editor.playmode start` | Enters Play Mode; `run-in-background` (default true) turns Run In Background on until Play Mode ends. Returns before the domain reload, so follow it with `unity status --until-ready`. |
| `editor.playmode stop` | Exits Play Mode. |
| `editor.playmode pause` | `paused` (default true); `false` resumes. |
| `editor.playmode set_run_in_background` | `enable` (default true) turns Run In Background on for this Editor session; `false` puts the project's value back (it does not force false). |
| `editor set_auto_tick` | `enable` (default true), `interval-ms` (default 16), `persist` (default true: survives domain reloads in this Editor session). See the note on auto tick below. |
| `script eval` | Compile and run C#: statements, leading `using`s, local functions, types, `await`; `return x;` or a trailing expression is the result. `code` or `file`, `timeout` (s) or `timeout-ms`, `dry-run`. Not supported: `auto-import`, `internals`, the `Wait` helper. |
| `test list` | `mode` all (default) \| editor \| playmode. One record per test: `FullName`, `Mode`, `Assembly`, `Categories`, `Explicit`. |
| `test run` | Runs tests in this Editor through `TestRunnerApi`. `mode` editor (default) \| playmode \| all (EditMode, then PlayMode), `filter`, `filter-type` TestName (case-insensitive part of the full name, default) \| Assembly \| Category, `async-tests` (default false), `timeout` (s, default 600). playmode and all need `async-tests`. Fails unless at least one test ran and none failed; the failing tests and their messages are in `errorDetails`. |
| `test status` | The last run started here: `status` running \| completed \| interrupted \| error (or `no_tests`), `summary`, `results` (`fullName`, `status`, `duration`, `message`, `stackTrace`). `detail` full (default) \| failures \| summary. A finished run that failed answers as a failure, like `test run`. Answered off the main thread; also served as `GET /api/test-status?detail=`. |
| `test cancel` | `NOT_SUPPORTED` while a run is going: com.unity.test-framework 1.1.33 cannot stop one. `{"status":"no_tests"}` otherwise. |
| `capture game_view` | PNG of the Game view. `source` screen (default: what the Game view drew, overlay UI included) \| camera (`Camera.main`, else the first enabled camera, off-screen at 1280x720), `save-path` or `output-path` (project-relative or absolute; under `Assets/` it is imported), `include-inline-image`, `max-resolution` (longest edge of the inline copy). With a path the reply is `savedPath`, `width`, `height`, `bytes` only; without one it carries the image as `base64` (`encoding: png`), which `unity mcp` passes on as an image. Fails with `NO_GPU` under `-nographics`. |
| `script run` | Compile one `.cs` file from anywhere on disk and call a static method; no asset import, no domain reload. `file`, `entry` (`Type.Method`; default: the only static `Run` or `Main`), `args` (JSON), `defines`, `timeout-ms`. Awaits a returned `Task`, `Task<T>`, `ValueTask`, `UniTask` or any other awaitable by its shape. The parameter names are this package's own: no public document lists the official ones. |

`script eval` compiles with the Roslyn compiler that ships with the Editor (`DotNetSdkRoslyn/csc.dll`,
run by `NetCoreRuntime/dotnet`), in its own process: about half a second per run, all of it compiling.
Each run loads one small assembly that cannot be unloaded until the next domain reload. Like the
official command it runs arbitrary code in the Editor; the bearer token is the only gate.

How a snippet is read (`Editor/EvalSource.cs`): `using` directives move to the top, type declarations
(including `record`, `delegate` and extension-method classes) move into the snippet's own namespace, the
rest runs as an async method, and a trailing expression is the result (a trailing `void` call returns
null). `Object` and `Random` mean the UnityEngine ones unless the script aliases them. Results: Unity
objects and Unity value types as their `ToString()`, collections and plain objects as JSON, framework
types (`Task`, `Type`, ...) as their `ToString()` so that nothing blocks reading them. Diagnostics read
`line L, col C: error ID: message` with a 0-based column, as the official package prints them.

`script run` compiles with the same compiler, with the Editor's script defines plus `defines`. One bindable
parameter takes the whole `args` value; more take a JSON array (by position) or object (by name). A
`CancellationToken` parameter gets the run's token, which the request timeout or a job cancel cancels.

Detached jobs: `unity command <command> --detach` submits any command as a job and prints its id;
`unity job status`, `unity job wait` and `unity job cancel` follow it (`GET /api/job?id=`, `POST /api/job/cancel`).
States are `queued`, `running`, `completed`, `failed` and `canceled`. At most 16 jobs can be unfinished (429
past that); the last 100 finished jobs are kept for an hour. A job is bounded only by the command's own
`timeout-ms` / `timeout` parameter, because the CLI sends no usable request timeout with `--detach`. Cancelling
a queued job means it never runs; a running one gets its token cancelled and ends `canceled` if it stops. The
job table survives domain reloads, and a job a reload cut short reads as `failed` (`Job Interrupted`).

Tests run inside the Editor that serves the request, so there is no second Editor and no `-runTests` exit
problem. A synchronous `test run` waits off the main thread while the Test Framework runs; the CLI's 30 s
request timeout applies, so pass `unity command --timeout <s>` for longer suites, or `--async-tests true` and
poll `test status`. Entering Play Mode reloads the domain: the run, with every result so far, is saved to
`SessionState` before each reload and the callbacks are registered again afterwards, which is why PlayMode runs
need `async-tests`. A synchronous run that a reload cuts off reads as `interrupted`. While a PlayMode run enters
Play Mode the server is briefly unreachable, so a script polling `unity command test status` should read the
message rather than stop at the first non-zero exit (a finished run that failed also exits non-zero). A run is refused while
another one is going, while the Editor is in Play Mode, and in an interactive Editor while a loaded scene has
unsaved changes (the Test Framework would stop at a save dialog). The commands exist when the project has
`com.unity.test-framework` 1.1.0 or newer; without it the package compiles without them (`test ...` is then an
unknown command) and leaves its own test assembly out.

`unity recompile` works end to end, including `--strict`, which fails on warnings produced by the compile.

Run In Background is a Player Setting that ships in builds. Before the first override in an Editor session the
server records the project's value in `SessionState`, and puts it back when the Editor returns to Edit Mode or
quits, so `ProjectSettings/ProjectSettings.asset` never keeps the override. A build made while it is overridden
does get the override.

Auto tick: while the Editor is unfocused, a timer posts a tick to the main thread that drains queued commands and
calls `EditorApplication.QueuePlayerLoopUpdate()`. Measured on macOS with 2022.3.62 it changes nothing you can
see: posts run on the Editor's own unfocused tick (about 10 per second), so queued commands still wait about
65 ms, and a game in Play Mode with Run In Background off stays at frame 1 while the Editor is unfocused, auto
tick or not. What keeps an unfocused game advancing is Run In Background, which `editor.playmode start` turns on
by default. The command exists because the CLI's play-mode recipe calls it.

CLI compatibility: the `unity` CLI 1.0.0-beta.13 and its skill docs use single-token names the public
Pipeline docs do not define. They work in argv and structured requests: `editor_status`, `console` (for
`console get`; a bare `unity command console` still lists the group), `console_status`, `clear_console`,
`eval`, `recompile`, `recompile_status`, `editor_play`, `editor_stop`, `editor_set_run_in_background`,
`set_autotick`, `script_run`, `run_tests`, `test_status`, `test_list`, `test_cancel`, `capture_game_view`, plus any
unambiguous `<group>_<name>`. Underscore parameter spellings (`since_session`,
`timeout_ms`) bind to the hyphenated parameter. The single-token name is also the catalog `name`, which
`unity mcp` uses as the MCP tool name, so it is unique. The official package rejects `console_status`, so
its `--strict` cannot read the console.

The CLI always sends a request timeout (30000 ms by default, `--timeout` seconds times 1000, 60000 ms from
`unity mcp`), and it wins over a command's own `timeout` (s) and `timeout-ms` parameters. So the skill docs'
`eval -- --timeout 15000` (meant as ms) is ignored through the CLI and the 30000 ms request timeout applies;
the parameters only decide for clients that send no request timeout, where `timeout` is read as seconds.

## Install

`unity pipeline install` refuses projects older than Unity 6.0, so install it by hand.

From Git, in `Packages/manifest.json` (append `#<tag>` to pin a release):

```json
"com.upipe.server": "https://github.com/sputnicyoji/upipe-unity.git"
```

Embedded: copy this folder to `<project>/Packages/com.upipe.server/`. Unity picks it up on the next
focus or launch. The server is on by default for everyone who opens the project; see Switch below.

Local path, for developing the package itself, in `Packages/manifest.json`:

```json
"com.upipe.server": "file:/absolute/path/to/Packages/com.upipe.server"
```

Do not install it next to `com.unity.pipeline` in the same project: both write
`Library/Pipeline/.unity-pipeline-port`.

## Use

Open the project in the Editor; the console logs `[Upipe] HTTP server listening on 127.0.0.1:<port>`.

```bash
unity command --project-path <project> editor status
```

## Switch

- `Tools/Upipe/Server Enabled` starts or stops the server in the running Editor. Off removes the descriptor
  and frees the port.
- The choice is saved in `<project>/UserSettings/UpipeSettings.json` (`{"enabled": false}`), which projects
  do not commit. No file means enabled.
- `UPIPE_ENABLE=1|true|0|false` in the Editor's environment overrides the file for that process and is
  never written back; the menu item is greyed out while it is set.

## Test

- Unit tests: add `"testables": ["com.upipe.server"]` to the project manifest and run the EditMode tests, for
  example in a running Editor: `unity command --timeout 600 run_tests -- --filter-type Assembly --filter Upipe.Editor.Tests`.
  Run that way, the test that stops and restarts the server skips itself (it would cut off the CLI's connection),
  and the console tests clear that Editor's console.
- Smoke test against a running Editor: `Tests~/smoke.sh <project> [path-to-unity-cli]`. Against an Editor
  started with `UPIPE_ENABLE=0`: `Tests~/smoke.sh --disabled <project>`.

Known limits:
- Enabled by default: committing the package to a shared repository turns the server on for every
  teammate until they switch it off locally.
- Verified only on macOS (`-batchmode` and the interactive Editor); not yet run on Windows
  (on Windows the descriptor file is not restricted to the owner).

Known issue: on 2022.3.62f3c1, `-batchmode -runTests` does not exit after writing results, with or
without this package installed. Read the results file and stop the process.

## Security model

Loopback only (`127.0.0.1`), `Host` pinned to `127.0.0.1:<port>`, requests carrying `Origin` refused,
bearer token on every route. The token is 256 random bits kept in `SessionState` and published only
in the descriptor file, which is written with mode 0600 (macOS/Linux).

## License

MIT, see `LICENSE.md`.
