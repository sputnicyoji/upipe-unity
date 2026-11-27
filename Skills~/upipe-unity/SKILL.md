---
name: upipe-unity
description: Drive running Unity 2022.3 Editors via com.upipe.server with the standalone official unity CLI (unity command / unity mcp). Use when automating or inspecting a Unity 2022.3 project, migrating workflows from third-party Unity MCP servers (scene/GameObject/asset edits, console, compile, tests, screenshots), or troubleshooting a 2022.3 setup where unity pipeline install is rejected.
---

# Upipe Unity (2022.3)

`com.upipe.server` brings the official `unity` CLI protocol to Unity 2022.3 as a pure Editor package (`127.0.0.1`, bearer token, no `com.unity.pipeline` code). Install it via `Packages/manifest.json` (`"com.upipe.server": "https://github.com/sputnicyoji/upipe-unity.git#v0.3.5"`) or embed in `Packages/com.upipe.server/` instead of running `unity pipeline install` (which rejects Unity < 6.0), and keep `com.unity.pipeline` absent from the same project so only one server owns `Library/Pipeline/.unity-pipeline-port`.

## Connect & target

The official `unity` CLI (`1.0.0-beta.13`) is distributed standalone and drives Unity 2022.3 directly without requiring Unity 6. Install it if missing (`which unity` / `Get-Command unity`):

```powershell
# Windows (PowerShell)
irm https://unity.com/install.ps1 | iex
```
```bash
# macOS / Linux
curl -fsSL https://unity.com/install.sh | bash
```

Prefer `unity command` in the shell (lower context overhead than MCP); if the client expects MCP tools, expose Upipe's 21 commands through `unity mcp configure <client> --project-path <dir>` (`unity mcp configure --list` shows supported clients).

```bash
unity status --format json                          # expect state "ready"
unity command --format json --project-path <dir>    # live command & parameter schema
```

- **Target explicitly**: pass `--project-path <canonical-full-dir>` (or export `UNITY_PROJECT_PATH`, noting `unity recompile` and `unity mcp configure` ignore `UNITY_PROJECT_PATH` and always need `--project-path <dir>`) whenever multiple Editors may be open. On Windows, resolve 8.3 short paths (e.g., `%TEMP%` containing `~1`) to their canonical long path so `unity status --until-ready` matches the descriptor's `projectPath`.
- **Single-token aliases & snake_case**: each command exposes a single-token alias such as `editor_play`, `eval`, `run_tests`, or `capture_scene_view` and accepts snake_case flags (`--timeout_ms`, `--since_session`); run `unity command --format json --project-path <dir>` for the live catalog.

## Switching from third-party Unity MCP servers

Rather than dozens of narrow CRUD tools (`manage_gameobject`, `manage_scene`, `manage_asset`), combine C# execution with Upipe's focused lifecycle commands:

| Task | Upipe + `unity` CLI command |
|---|---|
| Scene, hierarchy, prefab, asset, or component inspect/edit | `unity command eval '...'` or `unity command script run -- --file <path>` |
| Refresh assets & check compile errors | `unity recompile --strict --project-path <dir>` (or `script recompile`) |
| Read or clear Editor logs | `console get -- --tail 50 --level error`, `console clear` |
| Run EditMode / PlayMode tests | `test run -- --test-names Ns.Fixture`, `test status -- --detail failures` |
| Capture visual state | `capture game_view -- --save-path Temp/shot.png` or `capture scene_view` |
| Exit Editor cleanly | `editor quit` (refuses unsaved scenes / Play Mode unless `-- --force true`) |

## Core verification loops

1. **Code edit → compile → test loop** (done when `unity recompile` exits 0 and `test status` ends `completed` with `failed == 0` and empty `missingTestNames`):
   ```bash
   unity recompile --strict --project-path <dir>
   unity status --until-ready --project-path <dir>
   unity command --project-path <dir> --timeout 300 test run -- --test-names Ns.MyFixture
   ```
2. **Live Play Mode loop** (done only after confirming `frameCount` advances in `editor status` before capturing):
   ```bash
   unity command --project-path <dir> editor_play
   unity status --until-ready --project-path <dir>
   # confirm playMode == "playing" and frameCount advances across polls before trusting a frame
   unity command --json --project-path <dir> editor status
   unity command --project-path <dir> capture game_view -- --save-path Temp/play.png
   unity command --project-path <dir> editor_stop
   ```
   Entering Play Mode sets `run-in-background: true` for the session by default so unfocused Editors keep ticking (player builds restore the project's saved setting automatically). Save screenshots outside `Assets/` (`Temp/...`) unless intentionally importing a texture asset.

## Upipe-specific behavior & gotchas

- **CLI flag fence & timeouts**: put CLI options (`--project-path`, `--format json`, `--timeout <sec>`, `--detach`) **before** `--` and command options **after** `--`. The CLI's `--timeout` (seconds, default `30s`; MCP default `60s`) always overrides a command's own `timeout` (s) or `timeout-ms` parameter for non-detached calls. Use `--detach` + `unity job wait <id>` (bounded by the command's `timeout-ms` / `timeout`) for long runs; results >32 KB spill to `Temp/UpipeJobs/` across reloads.
- **`script eval` semantics & L2 cache**: leading `using`s hoist to file scope, types/records/delegates move into the snippet namespace, statements run async, and a trailing expression returns as JSON/`ToString()` (`void` returns `null`). Two-tier SHA-256 caching (64 in-memory LRU + 128 on disk in `Temp/UpipeEval/cache/`, including `void`-trailing forms) skips `csc.dll` on repeat runs while still loading a fresh isolated assembly per call. If a domain reload interrupts an active `eval`, Upipe returns a non-retryable failure so the CLI stops rather than re-running side effects.
- **`script run` (Upipe extension)**: compiles `.cs` files outside `Assets/` with active Editor defines (`-utf8output`, `-codepage:65001`), supporting `--file`, `--entry Type.Method` (falls back to parameterless non-UnityObject class instance `Run`/`Main`), `--args <json>`, `--defines`, `--timeout-ms`, and `--extra-files` (`;` or `,`-separated files/dirs). Injects `CancellationToken` and standard-library progress parameters (`IProgress<string>`, `IProgress<int>`, `IProgress<(string, int)>`, `Action<string>`, `Action<string, int>`) wired to `--detach` jobs.
- **Hot-Editor tests (`TestRunnerApi`)**: `mode playmode` and `mode all` (EditMode → PlayMode relay) require `--async-tests true`; domain reloads briefly drop HTTP connectivity, so keep polling `unity command --project-path <dir> test status` until status leaves `running`. Exact `--test-names` (`;` or top-level `,`) fails with `TEST_NAMES_NOT_RUN` if any name hits zero tests (including post-run `TEST_RUN_ERROR` cleanup errors after the final phase's `RunFinished`). In an interactive Editor, `test run` refuses unsaved scenes (`UNSAVED_SCENES`), so if an earlier `eval` dirtied an open scene, explicitly save it or confirm with the user before running tests. `test cancel` cancels the active Upipe run on `com.unity.test-framework` 1.4.0+ and restores starting scenes (1.1.33 answers `NOT_SUPPORTED`).
- **`capture` details**: `capture scene_view` renders the Scene camera off-screen (`--width` / `--height` 1–4096, default `1280x720`) or grabs the visible Scene tab (`--source screen`), returning `NO_SCENE_VIEW` in `-batchmode`; `NO_GPU` occurs under `-nographics`.

## When an Editor is unreachable

If no Editor process is running (`STATUS_NO_INSTANCES`), inspect installed Unity versions with `unity editors --installed` or launch the project via `unity open <dir>` (do not guess CLI subcommands) before waiting with `unity status --until-ready --project-path <dir>`. Otherwise check in order: (1) local opt-out (`UPIPE_ENABLE=0`, `<project>/UserSettings/UpipeSettings.json`, or `Tools/Upipe/Server Enabled`); (2) C# compile errors booting Unity into Safe Mode (Editor packages do not load—fix scripts and restart); (3) still importing/settling (`503` on `/api/commands` for 1.0 s on cold start or until the first idle tick after domain reload—run `unity status --until-ready`); (4) Windows 8.3 short path mismatch on `--project-path`. When a connection or lifecycle step stays broken, report the exact failure output and attempted checks directly rather than falling back to hand-editing `.unity` or `.prefab` YAML.
