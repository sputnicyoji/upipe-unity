#!/bin/bash
# Smoke test against a running Editor that has com.upipe.server installed.
# usage: smoke.sh [--disabled|--startup|--quit] <unity-project-path> [path-to-unity-cli]
# --disabled: the Editor was started with UPIPE_ENABLE=0 and its log already shows "[Upipe] Server disabled";
#             checks that nothing is listening for the project.
# --startup: launch the Editor first (a cold Library gives the longest settling window), then run this right away:
#            it waits for the descriptor and checks that `unity status --until-ready` waits for Upipe's own ready,
#            then goes on with the normal checks.
# --quit: only checks `editor quit`, which ends the Editor: an interactive Editor first refuses with an unsaved scene,
#         then --force quits; the process must exit and remove its descriptor.
# The test-name, progress and mode=all checks need the host fixtures: copy Tests~/HostFixtures into the project's Assets/
# (they are skipped otherwise). The package's own tests run too when the manifest lists it in "testables".
# Exits non-zero if any check fails. Never prints the token.
set -u
DISABLED=0
STARTUP=0
QUIT=0
[ "${1:-}" = "--disabled" ] && { DISABLED=1; shift; }
[ "${1:-}" = "--startup" ] && { STARTUP=1; shift; }
[ "${1:-}" = "--quit" ] && { QUIT=1; shift; }
PROJECT=${1:?usage: smoke.sh [--disabled|--startup|--quit] <unity-project-path> [unity-cli]}
CLI=${2:-$HOME/.unity/bin/unity}
DESCRIPTOR="$PROJECT/Library/Pipeline/.unity-pipeline-port"
FAILED=0

check() { # check <name> <expected> <actual>
  if [ "$2" = "$3" ]; then echo "PASS $1"; else echo "FAIL $1: expected [$2] got [$3]"; FAILED=1; fi
}

if [ $DISABLED = 1 ]; then
  check "disabled: no descriptor" absent "$([ -f "$DESCRIPTOR" ] && echo present || echo absent)"
  "$CLI" command --project-path "$PROJECT" editor status >/dev/null 2>&1; [ $? -ne 0 ]; check "disabled: cli cannot connect" 0 $?
  exit $FAILED
fi

field() { python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))[sys.argv[2]])' "$DESCRIPTOR" "$1"; }

if [ $STARTUP = 1 ]; then
  # A SIGTERM'd batchmode Editor leaves its descriptor (and token) behind, so wait for one whose writer is alive.
  for _ in $(seq 1 1200); do [ -f "$DESCRIPTOR" ] && kill -0 "$(field pid 2>/dev/null)" 2>/dev/null && break; sleep 0.5; done
fi
[ -f "$DESCRIPTOR" ] || { echo "FAIL descriptor missing: $DESCRIPTOR"; exit 1; }
PORT=$(field port)
TOKEN=$(field evalToken)
URL="http://127.0.0.1:$PORT"

# --- helpers (token passed through a file descriptor so it never appears in argv)
auth() { printf 'Authorization: Bearer %s\n' "$TOKEN"; }
code() { curl -s -o /dev/null -w '%{http_code}' "$@"; }
exec_json() { curl -s -H @<(auth) -H 'Content-Type: application/json' -d "$1" "$URL/api/exec"; }
json_get() { python3 -I -c 'import json,sys; d=json.loads(sys.stdin.read()); print(d.get(sys.argv[1]))' "$1"; }
# Evaluates an expression written in this script (never input from the server) against the parsed reply d.
json_eval() { python3 -I -c 'import json,sys; d=json.loads(sys.stdin.read()); print(eval(sys.argv[1]))' "$1"; }

check "descriptor is owner-only" "600" "$(stat -f '%Lp' "$DESCRIPTOR" 2>/dev/null || stat -c '%a' "$DESCRIPTOR")"

if [ $QUIT = 1 ]; then
  EDITOR_PID=$(field pid)
  if [ "$(field mode)" = editor ]; then
    # An interactive Editor must not drop unsaved work unless told to.
    exec_json '{"argv":["script","eval","var go = new UnityEngine.GameObject(\"UpipeSmokeDirty\"); UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene); return 1;"]}' >/dev/null
    check "quit: unsaved scene refused" True "$(exec_json '{"argv":["editor","quit"]}' | json_eval 'd["errorDetails"].startswith("UNSAVED_SCENES")')"
    check "quit: --confirm false still refused" True "$(exec_json '{"argv":["editor_quit","--confirm","false"]}' | json_eval 'd["errorDetails"].startswith("UNSAVED_SCENES")')"
  fi
  check "quit: bad exit-code refused" INVALID_COMMAND_ARGS "$(exec_json '{"argv":["editor","quit","--exit-code","x"]}' | json_get errorCode)"
  check "cli: editor_quit --force answers before quitting" "True 0" \
    "$("$CLI" command --json --project-path "$PROJECT" editor_quit -- --force 2>/dev/null | json_eval '"%s %s" % (d["data"]["result"]["quitting"], d["data"]["result"]["exitCode"])')"
  for _ in $(seq 1 120); do kill -0 "$EDITOR_PID" 2>/dev/null || break; sleep 0.5; done
  check "quit: the Editor process exited" gone "$(kill -0 "$EDITOR_PID" 2>/dev/null && echo alive || echo gone)"
  check "quit: descriptor removed" absent "$([ -f "$DESCRIPTOR" ] && echo present || echo absent)"
  exit $FAILED
fi

# The CLI calls an Editor ready when GET /api/commands answers 2xx and `starting` when it answers 503; it never reads
# the body of /api/status. So while Upipe is settling the catalog must be 503, and `status --until-ready` must not
# return before Upipe reports ready. The settling window may already be over when this starts (then only the end
# state is checked); a catalog that answered 200 must never be followed by a status of settling, since settled is one-way.
if [ $STARTUP = 1 ]; then
  EARLY_CATALOG=$(code -H @<(auth) "$URL/api/commands")
  EARLY_STATUS=$(curl -s -H @<(auth) "$URL/api/status" | json_get status 2>/dev/null)
  echo "NOTE startup: first probe saw /api/commands $EARLY_CATALOG, /api/status $EARLY_STATUS"
  [ "$EARLY_CATALOG" = 200 ] && check "startup: catalog 200 implies status ready" ready "$EARLY_STATUS"
  [ "$EARLY_STATUS" = settling ] && check "startup: status settling implies catalog 503" 503 "$EARLY_CATALOG"
  "$CLI" status --until-ready --timeout 900 --project-path "$PROJECT" >/dev/null 2>&1; check "startup: status --until-ready returns" 0 $?
  check "startup: status is ready once --until-ready returned" ready "$(curl -s -H @<(auth) "$URL/api/status" | json_get status 2>/dev/null)"
  check "startup: catalog is 200 once --until-ready returned" 200 "$(code -H @<(auth) "$URL/api/commands")"
fi

# The server reports "settling" until the Editor has been idle for a moment after startup.
for _ in $(seq 1 30); do
  curl -s -H @<(auth) "$URL/api/status" | grep -q '"status":"ready"' && break
  sleep 1
done

# --- official CLI
exec_json '{"argv":["script","eval","System.AppDomain.CurrentDomain.SetData(\"upipe-noop-recompile\", \"kept\"); return 1;"]}' >/dev/null
"$CLI" command --project-path "$PROJECT" editor status >/dev/null 2>&1; check "cli: editor status" 0 $?
"$CLI" command --project-path "$PROJECT" >/dev/null 2>&1; check "cli: list groups" 0 $?
"$CLI" command --project-path "$PROJECT" editor >/dev/null 2>&1; check "cli: list editor group" 0 $?
"$CLI" list --project-path "$PROJECT" >/dev/null 2>&1; check "cli: unity list (default full catalog)" 0 $?
"$CLI" command --project-path "$PROJECT" --detail compact >/dev/null 2>&1; check "cli: compact catalog" 0 $?
"$CLI" recompile --project-path "$PROJECT" >/dev/null 2>&1; check "cli: unity recompile (no changes)" 0 $?
check "cli: no-change recompile skipped domain reload" kept "$(exec_json '{"argv":["script","eval","return System.AppDomain.CurrentDomain.GetData(\"upipe-noop-recompile\");"]}' | json_get result)"
"$CLI" command --project-path "$PROJECT" console get --tail 5 >/dev/null 2>&1; check "cli: console get" 0 $?
check "cli: script eval" 4 "$("$CLI" command --project-path "$PROJECT" script eval --json 'return 2+2;' 2>/dev/null | json_eval 'd["data"]["result"]')"
check "cli: script eval honors UNITY_EDITOR define and UTF-8" "编辑_αβ" "$("$CLI" command --project-path "$PROJECT" script eval --json $'#if UNITY_EDITOR\nreturn "编辑_αβ";\n#else\nreturn "player";\n#endif' 2>/dev/null | json_eval 'd["data"]["result"]')"
# Passes on a clean compile, or on a failure over the host project's own warnings; never on a console read failure.
OUT=$("$CLI" recompile --project-path "$PROJECT" --strict 2>&1); E=$?
check "cli: recompile --strict reads the console" ok \
  "$( { [ $E -eq 0 ] || printf '%s' "$OUT" | grep -qi warning; } && ! printf '%s' "$OUT" | grep -q "Couldn't read the Editor console" && echo ok)"
check "cli: unknown command fails" yes "$("$CLI" command --project-path "$PROJECT" editor nope 2>&1 | grep -q 'Command Not Found' && echo yes)"
# Single-token names from the unity CLI skill docs; eval's --timeout there is in ms, and the CLI's own request timeout wins.
"$CLI" command --project-path "$PROJECT" editor_status >/dev/null 2>&1; check "cli: editor_status alias" 0 $?
"$CLI" command --project-path "$PROJECT" console_status >/dev/null 2>&1; check "cli: console_status alias" 0 $?
"$CLI" command --project-path "$PROJECT" console -- --tail 2 >/dev/null 2>&1; check "cli: console alias takes flags" 0 $?
check "cli: eval alias with the skill doc's ms timeout" 1 "$("$CLI" command --json --project-path "$PROJECT" eval -- --code 'return 1;' --timeout 15000 2>/dev/null | json_eval 'd["data"]["result"]')"
check "cli: unknown flag fails" yes "$("$CLI" command --project-path "$PROJECT" editor status --bogus 2>&1 | grep -q 'no parameter --bogus' && echo yes)"

# The test commands exist only when the project has com.unity.test-framework.
HAS_TESTS=$(exec_json '{"argv":["test","list","--mode","editor"]}' | json_get success 2>/dev/null)

# --- auto tick, Run In Background, console clear/write, play mode; leaves auto tick off and Run In Background as found
status_field() { curl -s -H @<(auth) "$URL/api/editor_status" | json_get "$1" 2>/dev/null; }
wait_field() { # wait_field <field> <value>: polls /api/editor_status, which stays answerable except during a reload
  for _ in $(seq 1 60); do [ "$(status_field "$1")" = "$2" ] && return 0; sleep 1; done; return 1; }
cli_result() { "$CLI" command --json --project-path "$PROJECT" "$@" 2>/dev/null; }
RIB=$(status_field runInBackground)
check "cli: set_autotick alias" "True 16 True" "$(cli_result set_autotick | json_eval '"%s %s %s" % (d["data"]["result"]["enabled"], d["data"]["result"]["intervalMs"], d["data"]["result"]["persist"])')"
check "cli: set_autotick snake_case flags" "32 False" "$(cli_result set_autotick -- --interval_ms 32 --persist false | json_eval '"%s %s" % (d["data"]["result"]["intervalMs"], d["data"]["result"]["persist"])')"
check "http: set_auto_tick interval-ms 0 rejected" "Parameter Validation Failed" "$(exec_json '{"argv":["editor","set_auto_tick","--interval-ms","0"]}' | json_get error)"
# Structured values are bound by argv's rules: a boolean that does not parse is refused before the handler runs.
check "http: structured set_autotick non-boolean enable" "Invalid Command Arguments" "$(exec_json '{"command":"set_autotick","parameters":{"enable":"maybe"}}' | json_get error)"
check "http: structured eval dry-run \"1\" is refused, not run" "Invalid Command Arguments" "$(exec_json '{"command":"eval","parameters":{"code":"return 1;","dry-run":"1"}}' | json_get error)"
check "cli: editor_set_run_in_background" "True True" "$(cli_result editor_set_run_in_background | json_eval '"%s %s" % (d["data"]["result"]["runInBackground"], d["data"]["result"]["overridden"])')"
check "cli: editor_set_run_in_background --enable false restores" "$RIB False" "$(cli_result editor_set_run_in_background -- --enable false | json_eval '"%s %s" % (d["data"]["result"]["runInBackground"], d["data"]["result"]["overridden"])')"
TAG="upipe-smoke-$$"
exec_json '{"argv":["console","write","'$TAG'-before"]}' >/dev/null
"$CLI" command --project-path "$PROJECT" clear_console >/dev/null 2>&1; check "cli: clear_console alias" 0 $?
"$CLI" command --project-path "$PROJECT" console write -- --message "$TAG-after" --level warn >/dev/null 2>&1; check "cli: console write" 0 $?
check "http: console clear keeps only later entries" "[('$TAG-after', 'warn')]" \
  "$(exec_json '{"argv":["console","get","--tail","2000"]}' | json_eval '[(e["message"], e["level"]) for e in d["result"]["entries"] if e["message"].startswith("'$TAG'")]')"
gt_warnings() { exec_json '{"command":"console_status"}' | json_eval 'd["result"]["groundTruth"]["consoleWarnings"]'; }
for _ in 1 2 3 4 5; do [ "$(gt_warnings)" != 0 ] && break; sleep 0.5; done # ground truth is sampled every 0.25 s
check "http: Editor console counted the written warning" True "$([ "$(gt_warnings)" -ge 1 ] 2>/dev/null && echo True || echo False)"
check "http: structured clear_console" True "$(exec_json '{"command":"clear_console"}' | json_get success)"
check "http: clear_console empties the Editor console too" 0 "$(gt_warnings)"
check "http: console write bad level" "Parameter Validation Failed" "$(exec_json '{"command":"console write","parameters":{"message":"x","level":"info"}}' | json_get error)"
check "http: console write needs message" INVALID_COMMAND_ARGS "$(exec_json '{"argv":["console","write"]}' | json_get errorCode)"
"$CLI" command --project-path "$PROJECT" console_write -- --message "upipe-alias-$$" >/dev/null 2>&1; check "cli: console_write alias" 0 $?
"$CLI" command --project-path "$PROJECT" editor_play >/dev/null 2>&1; check "cli: editor_play alias" 0 $?
"$CLI" status --until-ready --project-path "$PROJECT" --timeout 120 >/dev/null 2>&1; check "cli: unity status --until-ready after editor_play" 0 $?
wait_field playMode playing; check "play: playing" 0 $?
[ "$HAS_TESTS" = True ] && check "play: test run refused in Play Mode" True "$(exec_json '{"argv":["test","run","--filter","upipe-smoke"]}' | json_eval 'd["errorDetails"].startswith("PLAY_MODE_ACTIVE")')"
check "play: Run In Background on while playing" True "$(status_field runInBackground)"
F1=$(status_field frameCount); sleep 1; F2=$(status_field frameCount)
[ "${F2:-0}" -gt "${F1:-0}" ] 2>/dev/null; check "play: frameCount advances" 0 $?
check "play: playerLoopTicking" True "$(status_field playerLoopTicking)"
"$CLI" command --project-path "$PROJECT" editor_pause >/dev/null 2>&1; check "cli: editor_pause alias" 0 $?
wait_field playMode paused; check "play: paused" 0 $?
check "play: playerLoopTicking false while paused" False "$(status_field playerLoopTicking)"
"$CLI" command --project-path "$PROJECT" editor.playmode pause -- --paused false >/dev/null 2>&1
wait_field playMode playing; check "play: --paused false resumes" 0 $?
"$CLI" command --project-path "$PROJECT" editor_stop >/dev/null 2>&1; check "cli: editor_stop alias" 0 $?
wait_field playMode stopped; check "play: stopped" 0 $?
"$CLI" status --until-ready --project-path "$PROJECT" --timeout 120 >/dev/null 2>&1
exec_json '{"argv":["console","write","'$TAG'-after-play"]}' >/dev/null
wait_field runInBackground "$RIB"; check "play: Run In Background back to the project's value" 0 $?
check "cli: set_autotick --enable false" False "$(cli_result set_autotick -- --enable false | json_eval 'd["data"]["result"]["enabled"]')"
check "http: console capture survives leaving Play Mode" "['$TAG-after-play']" \
  "$(exec_json '{"argv":["console","get","--tail","50"]}' | json_eval '[e["message"] for e in d["result"]["entries"] if e["message"] == "'$TAG'-after-play"]')"

# --- raw HTTP

check "http: no token -> 401" 401 "$(code "$URL/api/status")"
check "http: wrong token -> 401" 401 "$(code -H 'Authorization: Bearer wrong' "$URL/api/status")"
check "http: Origin -> 403" 403 "$(code -H @<(auth) -H 'Origin: http://evil.example' "$URL/api/status")"
# The CLI switches Host to localhost:<port> after a refused connection during a reload; any other Host stays refused.
check "http: Host localhost -> 200" 200 "$(code -H @<(auth) -H "Host: localhost:$PORT" "$URL/api/status")"
check "http: other Host -> 400" 400 "$(code -H @<(auth) -H "Host: rebind.example:$PORT" "$URL/api/status")"
check "http: status ready" ready "$(curl -s -H @<(auth) "$URL/api/status" | json_get status)"
check "http: status reports non-negative lastTickAgeMs" True "$(curl -s -H @<(auth) "$URL/api/status" | json_eval 'isinstance(d.get("lastTickAgeMs"), int) and d["lastTickAgeMs"] >= 0')"
check "http: status has player-loop fields only in play mode" True "$(curl -s -H @<(auth) "$URL/api/status" | json_eval '("frameCount" in d) == ("playerLoopTicking" in d) == (d["playMode"] != "stopped")')"
check "http: editor_status needs token" 401 "$(code "$URL/api/editor_status")"
check "http: editor_status route" ready "$(curl -s -H @<(auth) "$URL/api/editor_status" | json_get status)"
check "http: editor_status reports the package version" "Upipe $(json_get version < "$(dirname "$0")/../package.json")" \
  "$(curl -s -H @<(auth) "$URL/api/editor_status" | json_get server)"
check "http: editor_status fields" True "$(curl -s -H @<(auth) "$URL/api/editor_status" | json_eval 'all(k in d for k in ("domainReloadInProgress", "autoRefresh", "runInBackground", "playMode", "lastTickAgeMs"))')"
check "http: argv editor_status alias" True "$(exec_json '{"argv":["editor_status"]}' | json_get success)"
check "http: argv console_status alias" True "$(exec_json '{"argv":["console_status"]}' | json_get success)"
check "http: structured eval alias" 2 "$(exec_json '{"command":"eval","parameters":{"code":"1 + 1"}}' | json_get result)"
check "http: argv --timeout_ms binds" 3 "$(exec_json '{"argv":["eval","--code","1 + 2","--timeout_ms","20000"]}' | json_get result)"
check "http: timeout-ms applies without a request timeout" TIMEOUT "$(exec_json '{"argv":["eval","--code","return 1;","--timeout-ms","1"]}' | json_get errorCode)"
check "http: structured full name" True "$(exec_json '{"command":"editor status"}' | json_get success)"
check "http: structured bare name + token" True "$(exec_json '{"command":"recompile_status","parameters":{"token":"x"}}' | json_get success)"
check "http: shared bare name is ambiguous" "Command Not Found" "$(exec_json '{"command":"status"}' | json_get error)"
check "http: console_status alias" True "$(exec_json '{"command":"console_status","parameters":{"token":"x"}}' | json_get success)"
check "http: console get shape" "['counts', 'cursor', 'dropped', 'entries', 'groundTruth', 'reset', 'returned', 'session']" \
  "$(exec_json '{"argv":["console","get","--tail","2"]}' | json_eval 'sorted(d["result"].keys())')"
check "http: console get bad tail" INVALID_COMMAND_ARGS "$(exec_json '{"argv":["console","get","--tail","x"]}' | json_get errorCode)"
check "http: eval trailing expression" 6 "$(exec_json '{"command":"script eval","parameters":{"code":"var x = 3; x * 2"}}' | json_get result)"
check "http: eval compile error" "COMPILATION_ERROR: Compilation Failed: 1 error(s)" \
  "$(exec_json '{"argv":["script","eval","return nope;"]}' | json_eval 'd["errorDetails"].split("\n")[0]')"
check "http: eval runtime error" True "$(exec_json '{"argv":["script","eval","throw new Exception(\"x\");"]}' | json_eval 'd["errorDetails"].startswith("RUNTIME_EXCEPTION")')"
check "http: eval needs code" "Parameter Validation Failed" "$(exec_json '{"argv":["script","eval"]}' | json_get error)"
check "http: structured dotted name" True "$(exec_json '{"command":"editor.status"}' | json_get success)"
check "http: structured unknown key" INVALID_COMMAND_ARGS "$(exec_json '{"command":"editor status","parameters":{"nope":1}}' | json_get errorCode)"
check "http: argv + parameters rejected" "Invalid Request" "$(exec_json '{"argv":["editor","status"],"parameters":{}}' | json_get error)"
check "http: bad json" "Invalid JSON" "$(exec_json '{oops' | json_get error)"
# The body limit, with a length up front and streamed in chunks (no length, so it is counted while read).
BIG=$(mktemp "${TMPDIR:-/tmp}/upipe-smoke-big-XXXXXX")
head -c $((4 * 1024 * 1024 + 1)) /dev/zero | tr '\0' 'a' > "$BIG"
check "http: body over 4 MB -> 413" 413 "$(code -H @<(auth) -H 'Content-Type: application/json' --data-binary @"$BIG" "$URL/api/exec")"
check "http: a 413 reply closes the connection" close "$(curl -s -o /dev/null -D - -H @<(auth) -H 'Content-Type: application/json' --data-binary @"$BIG" "$URL/api/exec" | tr -d '\r' | awk -F': *' 'tolower($1) == "connection" {print tolower($2)}')"
check "http: chunked body over 4 MB -> 413" 413 "$(code -H @<(auth) -H 'Content-Type: application/json' -H 'Transfer-Encoding: chunked' --data-binary @"$BIG" "$URL/api/exec")"
rm -f "$BIG"
check "http: huge timeout rejected cleanly" "Invalid Request" "$(exec_json '{"command":"editor status","timeout":99999999999999999999}' | json_get error)"
check "http: not found echoes typed name" nosuch "$(exec_json '{"argv":["nosuch","--x"]}' | json_get command)"
check "http: --= is emptyName" emptyName "$(exec_json '{"argv":["editor","status","--="]}' | json_eval 'd["argProblems"][0]["kind"]')"
check "http: structured not found omits command" None "$(exec_json '{"command":"nosuch"}' | json_get command)"
check "http: recompile_status shape" "['compilationFailed', 'errors', 'failed', 'status']" \
  "$(exec_json '{"command":"recompile_status","parameters":{"token":"x"}}' | json_eval 'sorted(d["result"].keys())')"

# --- /api/commands
catalog() { curl -s -H @<(auth) "$URL/api/commands$1"; }
check "catalog: default detail is full" True "$(catalog '' | json_eval '"schema" in d["commands"][0] and "parameters" in d["commands"][0]')"
check "catalog: compact drops schema" False "$(catalog '?detail=compact' | json_eval '"schema" in d["commands"][0]')"
check "catalog: bad detail -> 400" 400 "$(code -H @<(auth) "$URL/api/commands?detail=bogus")"
check "catalog: bad offset -> 400" 400 "$(code -H @<(auth) "$URL/api/commands?offset=-1")"
check "catalog: group is segment-aware" 0 "$(catalog '?group=edit' | json_get total)"
check "catalog: group match includes subgroups" 7 "$(catalog '?group=editor' | json_get total)"
check "catalog: capture group has game_view and scene_view" 2 "$(catalog '?group=capture' | json_get total)"
check "catalog: query is case-insensitive" 2 "$(catalog '?query=READY' | json_get total)"
check "catalog: names are unique (unity mcp tool names)" True "$(catalog '' | json_eval 'len({c["name"] for c in d["commands"]}) == len(d["commands"])')"
check "catalog: query matches the alias" 1 "$(catalog '?query=console_status' | json_get total)"
check "catalog: groups index" True "$(catalog '?detail=groups' | json_eval '"editor" in [g["group"] for g in d["groups"]]')"

# --- script run: a .cs file outside the project, compiled without an import or a domain reload
SCRIPTS=$(mktemp -d "${TMPDIR:-/tmp}/upipe-smoke-XXXXXX")
cat > "$SCRIPTS/Tool.cs" <<'CS'
using System.Threading;
using System.Threading.Tasks;
public static class Tool
{
    public static int Add(int a, int b) => a + b;
    public static async Task<int> Slow(int milliseconds, CancellationToken token)
    {
        await Task.Delay(milliseconds, token);
        return milliseconds;
    }
    public static string Defined()
    {
#if UPIPE_SMOKE && UNITY_EDITOR
        return "both";
#else
        return "missing";
#endif
    }
}
CS
printf 'public static class Broken { public static int Run() => nope; }\n' > "$SCRIPTS/Broken.cs"
cat > "$SCRIPTS/ReloadNow.cs" <<'CS'
public static class ReloadNow
{
    public static string Run()
    {
        System.AppDomain.CurrentDomain.SetData("upipe-smoke", "old"); // gone in the next domain
        UnityEditor.EditorUtility.RequestScriptReload();
        return "requested";
    }
}
CS
mkdir -p "$PROJECT/Temp/upipe-smoke"; cp "$SCRIPTS/Tool.cs" "$PROJECT/Temp/upipe-smoke/Tool.cs"
check "cli: script run sync entry with JSON args" 5 "$(cli_result script run "$SCRIPTS/Tool.cs" --entry Tool.Add --args '[2,3]' | json_eval 'd["data"]["result"]')"
check "cli: script_run alias awaits Task<int>" 30 "$(cli_result script_run -- "$SCRIPTS/Tool.cs" --entry Tool.Slow --args 30 | json_eval 'd["data"]["result"]')"
check "cli: script run --defines plus the Editor's defines" both "$(cli_result script run "$SCRIPTS/Tool.cs" --entry Tool.Defined --defines 'UPIPE_SMOKE;OTHER' | json_eval 'd["data"]["result"]')"
check "http: script run Windows-style relative path" 2 "$(exec_json '{"argv":["script","run","Temp\\upipe-smoke\\Tool.cs","--entry","Tool.Add","--args","[1,1]"]}' | json_get result)"
check "http: structured script run with JSON args" 7 "$(exec_json '{"command":"script run","parameters":{"file":"'"$SCRIPTS"'/Tool.cs","entry":"Tool.Add","args":{"a":3,"b":4}}}' | json_get result)"
check "http: script run compile error" "COMPILATION_ERROR: Compilation Failed: 1 error(s)" \
  "$(exec_json '{"argv":["script","run","'"$SCRIPTS"'/Broken.cs"]}' | json_eval 'd["errorDetails"].split("\n")[0]')"
check "http: script run missing file" "Parameter Validation Failed" "$(exec_json '{"argv":["script","run","'"$SCRIPTS"'/nope.cs"]}' | json_get error)"
check "http: script run needs a file" INVALID_COMMAND_ARGS "$(exec_json '{"argv":["script","run"]}' | json_get errorCode)"

# --- script run with --extra-files (files and directories) and a legacy instance entry
mkdir -p "$SCRIPTS/lib/sub" "$SCRIPTS/lib/.hidden"
printf 'public static class Multi { public static int Run() => Helpers.Twice(Deep.Value); }\n' > "$SCRIPTS/Multi.cs"
printf 'public static class Helpers { public static int Twice(int x) => 2 * x; public static string Run() => "helper"; }\n' > "$SCRIPTS/lib/Helpers.cs"
printf 'public static class Deep { public const int Value = 21; }\n' > "$SCRIPTS/lib/sub/Deep.cs"
printf 'this is not C#\n' > "$SCRIPTS/lib/.hidden/Broken.cs"
printf 'public static class BadHelper\n{\n    public static int Value => nope;\n}\n' > "$SCRIPTS/BadHelper.cs"
cat > "$SCRIPTS/Legacy.cs" <<'CS'
public interface IEntrance { void Main(); }
public class LegacyEntrance : IEntrance
{
    public void Main() => UnityEngine.Debug.Log("upipe-smoke-legacy-main");
}
CS
printf 'public class Behaviour : UnityEngine.MonoBehaviour { public void Main() { } }\n' > "$SCRIPTS/Behaviour.cs"
# Each file is compiled under a #line directive; a #define on its first line must still count.
printf '#define UPIPE_SMOKE_FIRST\npublic static class DefineFirst { public static string Run() =>\n#if UPIPE_SMOKE_FIRST\n "defined";\n#else\n "missing";\n#endif\n}\n' > "$SCRIPTS/DefineFirst.cs"
# The CLI refuses a repeated flag, so its callers give one with ; or , between paths; the main file named again is dropped.
check "cli: script run --extra-files directory, default entry from the main file" 42 \
  "$(cli_result script run "$SCRIPTS/Multi.cs" --extra-files "$SCRIPTS/lib;$SCRIPTS/Multi.cs" | json_eval 'd["data"]["result"]')"
check "cli: script run --entry in an extra file" helper "$(cli_result script run "$SCRIPTS/Multi.cs" --extra-files "$SCRIPTS/lib" --entry Helpers.Run | json_eval 'd["data"]["result"]')"
check "http: argv repeats --extra-files" 42 "$(exec_json '{"argv":["script","run","'"$SCRIPTS"'/Multi.cs","--extra-files","'"$SCRIPTS"'/lib/Helpers.cs","--extra-files","'"$SCRIPTS"'/lib/sub"]}' | json_get result)"
check "http: structured extra_files array" 42 "$(exec_json '{"command":"script_run","parameters":{"file":"'"$SCRIPTS"'/Multi.cs","extra_files":["'"$SCRIPTS"'/lib"]}}' | json_get result)"
check "http: an error in an extra file names it" True "$(exec_json '{"argv":["script","run","'"$SCRIPTS"'/Tool.cs","--entry","Tool.Add","--args","[1,1]","--extra-files","'"$SCRIPTS"'/BadHelper.cs"]}' | json_eval 'd["errorDetails"].split("\n")[1].startswith("BadHelper.cs: line 3, col 31: error CS0103")')"
check "http: script run missing extra file" "Parameter Validation Failed" "$(exec_json '{"argv":["script","run","'"$SCRIPTS"'/Multi.cs","--extra-files","'"$SCRIPTS"'/nope.cs"]}' | json_get error)"
check "cli: legacy instance Main runs on a new instance" True "$(cli_result script run "$SCRIPTS/Legacy.cs" | json_eval 'd["success"]')"
check "http: the legacy Main logged" True "$(exec_json '{"argv":["console","get","--tail","50"]}' | json_eval 'any(e["message"] == "upipe-smoke-legacy-main" for e in d["result"]["entries"])')"
check "http: instance entry on a MonoBehaviour refused" "Parameter Validation Failed" "$(exec_json '{"argv":["script","run","'"$SCRIPTS"'/Behaviour.cs"]}' | json_get error)"
check "http: script run keeps a #define on the first line" defined "$(exec_json '{"argv":["script","run","'"$SCRIPTS"'/DefineFirst.cs","--extra-files","'"$SCRIPTS"'/lib"]}' | json_get result)"

# --- detached jobs: unity command --detach, unity job status / wait / cancel
job_json() { curl -s -H @<(auth) "$URL/api/job?id=$1"; }
job_state() { job_json "$1" | json_get state 2>/dev/null; }
wait_job_state() { for _ in $(seq 1 120); do [ "$(job_state "$1")" = "$2" ] && return 0; sleep 0.5; done; return 1; }
detach() { cli_result "$@" --detach | json_eval 'd["data"]["jobId"]'; }
job_cli() { "$CLI" job "$1" "$2" --project-path "$PROJECT" --format json 2>&1; }
J1=$(detach script eval 'return 6*7;')
check "cli: --detach returns a job id" True "$([ -n "$J1" ] && [ "$J1" != None ] && echo True || echo False)"
check "cli: job wait prints the result" "completed 42" "$(job_cli wait "$J1" | json_eval '"%s %s" % (d["data"]["state"], d["data"]["result"])')"
check "cli: job status" "completed script eval False" "$(job_cli status "$J1" | json_eval '"%s %s %s" % (d["data"]["state"], d["data"]["command"], d["data"]["cancellationRequested"])')"
J2=$(detach script run "$SCRIPTS/Tool.cs" --entry Tool.Slow --args 600000)
wait_job_state "$J2" running; check "job: long script run is running" 0 $?
check "cli: job cancel requests cancellation" True "$(job_cli cancel "$J2" | json_eval 'd["data"]["cancellationRequested"]')"
OUT=$(job_cli wait "$J2"); E=$?
check "cli: job wait on a cancelled job fails" "6 True" "$E $(echo "$OUT" | json_eval '"was canceled" in d["errors"][0]["message"]')"
check "cli: job status canceled" canceled "$(job_cli status "$J2" | json_eval 'd["data"]["state"]')"
J3=$(detach script run "$SCRIPTS/Tool.cs" --entry Tool.Slow --args 600000 --timeout-ms 300)
OUT=$(job_cli wait "$J3"); E=$?
check "cli: --timeout-ms bounds a detached job" "6 True" "$E $(echo "$OUT" | json_eval '"failed: Request Timed Out" in d["errors"][0]["message"]')"
check "cli: job status unknown id fails" yes "$("$CLI" job status nope --project-path "$PROJECT" 2>&1 | grep -q 'Job Not Found' && echo yes)"
check "http: job status unknown id -> 404" "404 Job Not Found" "$(code -H @<(auth) "$URL/api/job?id=nope") $(job_json nope | json_get error)"
check "http: job status needs an id" 400 "$(code -H @<(auth) "$URL/api/job")"
check "http: job status needs the token" 401 "$(code "$URL/api/job?id=$J1")"
check "http: job cancel unknown id -> 404" 404 "$(code -H @<(auth) -H 'Content-Type: application/json' -d '{"id":"nope"}' "$URL/api/job/cancel")"
check "http: job cancel needs an id" 400 "$(code -H @<(auth) -H 'Content-Type: application/json' -d '{}' "$URL/api/job/cancel")"
check "http: group listing cannot be a job" "Invalid Request" "$(exec_json '{"argv":["editor"],"job":true}' | json_get error)"
check "http: bad arguments refused at submission" INVALID_COMMAND_ARGS "$(exec_json '{"argv":["editor","status","--bogus"],"job":true}' | json_get errorCode)"
J5=$(exec_json '{"command":"editor_status","job":true}' | json_eval 'd["result"]["jobId"]')
wait_job_state "$J5" completed; check "http: structured job completes" ready "$(job_json "$J5" | json_eval 'd["result"]["status"]')"
# A script reports progress through a standard library parameter; job status shows it while running, never after.
cat > "$SCRIPTS/Progress.cs" <<'CS'
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
public static class Progressing
{
    public static async Task<string> Run(string go, IProgress<(string, int)> progress, CancellationToken token)
    {
        progress.Report(("halfway", 50));
        while (!File.Exists(go))
            await Task.Delay(20, token);
        return "done";
    }
}
CS
J6=$(detach script run "$SCRIPTS/Progress.cs" --args "\"$SCRIPTS/go\"")
for _ in $(seq 1 120); do [ "$(job_json "$J6" | json_eval 'd.get("progress") and d["progress"]["message"]')" = halfway ] && break; sleep 0.5; done
check "http: job reports progress while running" "running halfway 50" "$(job_json "$J6" | json_eval '"%s %s %s" % (d["state"], d["progress"]["message"], d["progress"]["percent"])')"
check "cli: job status shows progress" "halfway 50" "$(job_cli status "$J6" | json_eval '"%s %s" % (d["data"]["progress"]["message"], d["data"]["progress"]["percent"])')"
touch "$SCRIPTS/go"
check "cli: job wait after progress" "completed done" "$(job_cli wait "$J6" | json_eval '"%s %s" % (d["data"]["state"], d["data"]["result"])')"
check "http: finished job has no progress" False "$(job_json "$J6" | json_eval '"progress" in d')"

# --- tests in this Editor (TestRunnerApi): test list / run / status / cancel, GET /api/test-status
if [ "$HAS_TESTS" = True ]; then
  check "http: capabilities include tests.camelCase" True "$(curl -s -H @<(auth) "$URL/api/status" | json_eval '"tests.camelCase" in d.get("capabilities", [])')"
  test_status_json() { curl -s -H @<(auth) "$URL/api/test-status$1"; }
  # Polls GET /api/test-status (answerable while a PlayMode run reloads the domain) until the run is no longer running.
  wait_test_done() { for _ in $(seq 1 120); do TS=$(test_status_json '?detail=summary' | json_get status 2>/dev/null); [ -n "$TS" ] && [ "$TS" != running ] && return 0; sleep 0.5; done; return 1; }
  # Polls until the running run has at least one result, in the given phase if one is named.
  wait_test_result() { for _ in $(seq 1 120); do [ "$(test_status_json '?detail=summary' | json_eval '"%s %s" % (d["status"] == "running" and d["summary"]["total"] >= 1, d.get("phase"))' 2>/dev/null)" = "True ${1:-EditMode}" ] && return 0; sleep 0.25; done; return 1; }
  # test cancel is compiled in when Upipe's asmdef defines UPIPE_TEST_CANCEL (test-framework 1.4.0 or newer). Asking the server
  # instead of reading packages-lock.json reads a pre-release, local or embedded test-framework the way the asmdef does.
  CANCEL_API=$(exec_json '{"argv":["script","eval","return System.Type.GetType(\"Upipe.Editor.TestCommands, Upipe.Editor\")?.GetMethod(\"CancelFrameworkJob\", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static) != null;"]}' | json_get result)
  LIST=$(cli_result test list)
  check "cli: test list records" True "$(echo "$LIST" | json_eval 'all(set(t) == {"FullName", "Mode", "Assembly", "Categories", "Explicit"} for t in d["data"]["result"])')"
  OWN=$(echo "$LIST" | json_eval 'sum(t["FullName"].startswith("Upipe.Editor.Tests.TestCommandsTests.") for t in d["data"]["result"])')
  if [ "${OWN:-0}" -gt 0 ] 2>/dev/null; then
    check "cli: test run waits for an EditMode run" "completed editor $OWN $OWN 0" "$(cli_result --timeout 300 test run -- --filter Upipe.Editor.Tests.TestCommandsTests | json_eval '"%s %s %s %s %s" % (d["data"]["result"]["status"], d["data"]["result"]["mode"], d["data"]["result"]["summary"]["total"], d["data"]["result"]["summary"]["passed"], d["data"]["result"]["summary"]["failed"])')"
    check "cli: run_tests summary line" "$OWN/$OWN passed (editor)" "$("$CLI" command --project-path "$PROJECT" --timeout 300 run_tests -- --filter testcommandstests 2>/dev/null | tail -1 | cut -f3)"
    check "cli: test_status summary line" "completed: $OWN/$OWN passed" "$("$CLI" command --project-path "$PROJECT" test_status 2>/dev/null | tail -1 | cut -f3)"
    check "http: test-status results are the run's tests" True "$(test_status_json '' | json_eval 'len(d["results"]) == '"$OWN"' and all(set(r) == {"fullName", "status", "duration", "message", "stackTrace"} for r in d["results"])')"
  else
    echo "SKIP test run of Upipe's own tests: the manifest lacks \"testables\": [\"com.upipe.server\"]"
  fi
  NOMATCH=$(exec_json '{"argv":["test","run","--filter","upipe-smoke-no-such-test"]}')
  check "http: test run matching nothing fails" "Parameter Validation Failed True" "$(echo "$NOMATCH" | json_eval '"%s %s" % (d["error"], "No test ran" in d["errorDetails"])')"
  check "http: the failure carries the run" "completed 0" "$(echo "$NOMATCH" | json_eval '"%s %s" % (d["result"]["status"], d["result"]["summary"]["total"])')"
  check "http: synchronous playmode run refused" "Parameter Validation Failed" "$(exec_json '{"argv":["test","run","--mode","playmode"]}' | json_get error)"
  check "http: bad test mode" "Parameter Validation Failed" "$(exec_json '{"argv":["run_tests","--mode","player"]}' | json_get error)"
  check "http: bad filter-type" "Parameter Validation Failed" "$(exec_json '{"argv":["test","run","--filter-type","regex"]}' | json_get error)"
  check "http: test status summary leaves out results" "Parameter Validation Failed False" "$(exec_json '{"command":"test_status","parameters":{"detail":"summary"}}' | json_eval '"%s %s" % (d["error"], "results" in d["result"])')"
  check "http: test status bad detail" "Parameter Validation Failed" "$(exec_json '{"argv":["test","status","--detail","brief"]}' | json_get error)"
  check "http: test-status route" "completed editor" "$(test_status_json '?detail=summary' | json_eval '"%s %s" % (d["status"], d["mode"])')"
  check "http: test-status bad detail -> 400" 400 "$(code -H @<(auth) "$URL/api/test-status?detail=brief")"
  check "http: test-status needs the token" 401 "$(code "$URL/api/test-status")"
  check "cli: test cancel with no run going" no_tests "$(cli_result test cancel | json_eval 'd["data"]["result"]["status"]')"
  # PlayMode needs async-tests: entering Play Mode reloads the domain, and the run must still report afterwards.
  START=$(exec_json '{"argv":["run_tests","--mode","playmode","--async-tests","true","--filter","upipe-smoke-no-such-test"]}')
  check "http: async playmode run answers running, without counts" "running playmode False" "$(echo "$START" | json_eval '"%s %s %s" % (d["result"]["status"], d["result"]["mode"], "summary" in d["result"])')"
  if [ "$CANCEL_API" != True ]; then
    check "http: test cancel is not supported before test-framework 1.4" True "$(exec_json '{"argv":["test","cancel"]}' | json_eval 'd["errorDetails"].startswith("NOT_SUPPORTED: canceling a test run needs com.unity.test-framework 1.4.0")')"
  fi
  wait_test_done
  check "play: the async playmode run reports across the reload" "completed playmode 0" "$(test_status_json '?detail=summary' | json_eval '"%s %s %s" % (d["status"], d["mode"], d["summary"]["total"])')"
  wait_field playMode stopped; check "play: back in Edit Mode after the run" 0 $?
  "$CLI" status --until-ready --project-path "$PROJECT" --timeout 120 >/dev/null 2>&1
  SMOKE_MODES=$(echo "$LIST" | json_eval 'len({t["Mode"] for t in d["data"]["result"] if "SmokePasses" in t["FullName"]})')
  if [ "${SMOKE_MODES:-0}" = 2 ]; then
    ALL_START=$(exec_json '{"argv":["run_tests","--mode","all","--async-tests","true","--filter","SmokePasses"]}')
    check "http: async mode=all starts" "running all" "$(echo "$ALL_START" | json_eval '"%s %s" % (d.get("result", {}).get("status"), d.get("result", {}).get("mode"))')"
    wait_test_done
    check "all: mode=all relays EditMode -> PlayMode" "completed all 2 2 0" \
      "$(test_status_json '?detail=summary' | json_eval '"%s %s %s %s %s" % (d.get("status"), d.get("mode"), d.get("summary", {}).get("total"), d.get("summary", {}).get("passed"), d.get("summary", {}).get("failed"))')"
    wait_field playMode stopped; check "all: back in Edit Mode after mode=all" 0 $?
    "$CLI" status --until-ready --project-path "$PROJECT" --timeout 120 >/dev/null 2>&1
    if [ "$(echo "$LIST" | json_eval 'any(t["FullName"] == "UpipeFixtures.Play.SmokePlay.SmokePasses" for t in d["data"]["result"])')" = True ]; then
      # A PlayMode-only name is checked once both halves ran, not missed after the EditMode half.
      exec_json '{"argv":["run_tests","--mode","all","--async-tests","true","--test-names","UpipeFixtures.SmokeEdit.SmokePasses;UpipeFixtures.Play.SmokePlay.SmokePasses"]}' >/dev/null
      wait_test_done
      check "all: test-names across both halves" "completed 2 []" \
        "$(test_status_json '?detail=summary' | json_eval '"%s %s %s" % (d.get("status"), d.get("summary", {}).get("passed"), d.get("missingTestNames"))')"
      wait_field playMode stopped
      "$CLI" status --until-ready --project-path "$PROJECT" --timeout 120 >/dev/null 2>&1
    fi
  else
    echo "SKIP mode=all SmokePasses: host project has no EditMode+PlayMode SmokePasses pair"
  fi
  # --test-names: exact names (a fixture or parameterized method runs all of it), and any name that ran nothing fails the run.
  if [ "$(echo "$LIST" | json_eval 'any(t["FullName"] == "UpipeFixtures.CalcTests.Add(1,2)" for t in d["data"]["result"])')" = True ]; then
    run_names() { cli_result --timeout 300 test run -- --test-names "$1"; }
    check "cli: test run --test-names group, fixture and case" "completed 3 3" \
      "$(run_names 'UpipeFixtures.CalcTests.Add;UpipeFixtures.OtherFixture.Lone' | json_eval '"%s %s %s" % (d["data"]["result"]["status"], d["data"]["result"]["summary"]["total"], d["data"]["result"]["summary"]["passed"])')"
    check "cli: test run --test-names keeps commas inside parentheses and quotes" 2 \
      "$(run_names 'UpipeFixtures.CalcTests.Quote("a,b"),UpipeFixtures.CalcTests.Add(1,2)' | json_eval 'd["data"]["result"]["summary"]["passed"]')"
    MISSED=$(exec_json '{"argv":["test","run","--test-names","UpipeFixtures.CalcTests.Add;UpipeFixtures.CalcTests.Ad"]}')
    check "http: a test name that ran nothing fails the run" "Parameter Validation Failed True 2" \
      "$(echo "$MISSED" | json_eval '"%s %s %s" % (d["error"], "TEST_NAMES_NOT_RUN: 1 of 2" in d["errorDetails"] and "UpipeFixtures.CalcTests.Ad" in d["errorDetails"], d["result"]["summary"]["passed"])')"
    check "http: test status keeps failing on the missed name" "Parameter Validation Failed ['UpipeFixtures.CalcTests.Ad']" \
      "$(exec_json '{"command":"test_status","parameters":{"detail":"summary"}}' | json_eval '"%s %s" % (d["error"], d["result"]["missingTestNames"])')"
    "$CLI" command --project-path "$PROJECT" --timeout 300 test run -- --test-names 'UpipeFixtures.CalcTests.Nope' >"$SCRIPTS/missed.out" 2>&1; E=$?
    check "cli: test run with a misspelled name exits non-zero" "yes yes" \
      "$([ $E -ne 0 ] && echo yes) $(grep -q 'TEST_NAMES_NOT_RUN' "$SCRIPTS/missed.out" && echo yes)"
    check "http: test-names with filter refused" "Parameter Validation Failed" "$(exec_json '{"argv":["test","run","--filter","Calc","--test-names","UpipeFixtures.CalcTests.Add"]}' | json_get error)"
    check "http: structured test_names array" 2 "$(exec_json '{"command":"run_tests","parameters":{"test_names":["UpipeFixtures.CalcTests.Add"]},"timeout":300000}' | json_eval 'd["result"]["summary"]["passed"]')"
    # A detached synchronous run reports finished/total tests as the job's progress.
    J7=$(detach test run --test-names UpipeFixtures.SlowFixture)
    P7=""; for _ in $(seq 1 120); do P7=$(job_json "$J7" | json_eval 'd["progress"]["message"] if d.get("progress") else ""'); echo "$P7" | grep -q '^[1-3]/4' && break; sleep 0.25; done
    check "job: detached test run reports progress" True "$(echo "$P7" | grep -Eq '^[1-3]/4 tests finished \(EditMode\)$' && echo True || echo "False ($P7) $(job_json "$J7")")"
    OUT=$(job_cli wait "$J7")
    check "cli: detached test run completes" "completed 4" "$(echo "$OUT" | json_eval '"%s %s" % (d["data"]["state"], d["data"]["result"]["summary"]["passed"])' 2>/dev/null || echo "$OUT" | head -c 400)"
    if [ "$(echo "$LIST" | json_eval 'any(t["FullName"] == "UpipeFixtures.ReloadFixture.ReloadsTheDomain" for t in d["data"]["result"])')" = True ]; then
      # A synchronous run whose test reloads the domain: the waiting request is answered as cut off, and the CLI does not resend it.
      "$CLI" command --project-path "$PROJECT" --timeout 300 test run -- --test-names UpipeFixtures.ReloadFixture.ReloadsTheDomain >"$SCRIPTS/reload-run.out" 2>&1; E=$?
      check "cli: a synchronous run cut off by a domain reload is not retried" "yes yes" \
        "$([ $E -ne 0 ] && echo yes) $(grep -q 'TEST_RUN_INTERRUPTED' "$SCRIPTS/reload-run.out" && echo yes)"
      "$CLI" status --until-ready --project-path "$PROJECT" --timeout 120 >/dev/null 2>&1
      # The framework finishes the resumed test after the reload; until then a new run is refused as in progress.
      check "cli: a test run after the cut-off run passes" "completed 1" "$(for _ in $(seq 1 30); do R=$(cli_result --timeout 300 test run -- --test-names UpipeFixtures.SmokeEdit.SmokePasses | json_eval '"%s %s" % (d["data"]["result"]["status"], d["data"]["result"]["summary"]["passed"])' 2>/dev/null); [ "$R" = "completed 1" ] && break; sleep 1; done; echo "$R")"
    else
      echo "SKIP synchronous run cut off by a reload: the host's HostFixtures copy lacks UpipeFixtures.ReloadFixture; copy Tests~/HostFixtures again"
    fi
    if [ "$CANCEL_API" = True ]; then
      # test cancel once the first test is in: the run ends canceled with only the tests that finished.
      exec_json '{"argv":["run_tests","--async-tests","true","--test-names","UpipeFixtures.SlowFixture"]}' >/dev/null
      wait_test_result
      check "cli: test cancel stops an async EditMode run" "canceled editor True" \
        "$(cli_result test cancel | json_eval '"%s %s %s" % (d["data"]["result"]["status"], d["data"]["result"]["mode"], 1 <= d["data"]["result"]["summary"]["total"] < 4)')"
      check "http: test status fails a canceled run" "Command Execution Failed True" \
        "$(exec_json '{"argv":["test","status","--detail","summary"]}' | json_eval '"%s %s" % (d["error"], d["errorDetails"].startswith("TEST_RUN_CANCELED"))')"
      # Left counting as running, the framework's runner logs an error at the next domain reload.
      check "eval: a canceled EditMode run leaves no test runner running" 0 \
        "$(exec_json '{"argv":["script","eval","var t = System.Type.GetType(\"UnityEditor.TestTools.TestRunner.EditModeRunner, UnityEditor.TestRunner\"); var f = t == null ? null : t.GetField(\"RunningTests\", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance); if (f == null) return \"unresolved\"; var n = 0; foreach (var o in UnityEngine.Resources.FindObjectsOfTypeAll(t)) if ((bool)f.GetValue(o)) n++; return n;"]}' | json_get result)"
      # A detached synchronous run: the waiting request answers the canceled run, so its job fails.
      J8=$(detach test run --test-names UpipeFixtures.SlowFixture)
      wait_test_result
      check "http: test_cancel stops a detached test run" canceled "$(exec_json '{"argv":["test_cancel"]}' | json_eval 'd["result"]["status"]')"
      for _ in $(seq 1 40); do [ "$(job_state "$J8")" != running ] && break; sleep 0.25; done
      check "job: a canceled test run fails its job" "failed True" "$(job_json "$J8" | json_eval '"%s %s" % (d["state"], d["errorDetails"].startswith("TEST_RUN_CANCELED"))')"
      # mode all canceled in its EditMode half does not go on to the PlayMode half.
      exec_json '{"argv":["run_tests","--mode","all","--async-tests","true","--test-names","UpipeFixtures.SlowFixture;UpipeFixtures.Play.SlowPlay"]}' >/dev/null
      wait_test_result
      check "all: mode=all canceled in its EditMode half skips Play Mode" "canceled all stopped" \
        "$(exec_json '{"argv":["test","cancel"]}' | json_eval '"%s %s" % (d["result"]["status"], d["result"]["mode"])') $(status_field playMode)"
      # Past the point where the PlayMode half would have started: still canceled, no PlayMode result, never in Play Mode.
      sleep 3
      check "all: a canceled mode=all never starts its PlayMode half" "canceled 0 stopped" \
        "$(test_status_json '?detail=full' | json_eval '"%s %s" % (d["status"], sum(r["fullName"].startswith("UpipeFixtures.Play.") for r in d.get("results", [])))') $(status_field playMode)"
      if [ "$(echo "$LIST" | json_eval 'any(t["FullName"] == "UpipeFixtures.Play.SlowPlay.First" for t in d["data"]["result"])')" = True ]; then
        # Canceled at once, on its way into Play Mode: at any moment the run ends canceled, out of Play Mode.
        exec_json '{"argv":["run_tests","--mode","playmode","--async-tests","true","--test-names","UpipeFixtures.Play.SlowPlay"]}' >/dev/null
        for _ in $(seq 1 60); do [ "$(exec_json '{"argv":["test","cancel"]}' | json_get success 2>/dev/null)" = True ] && break; sleep 0.25; done
        wait_test_done; wait_field playMode stopped
        check "play: a PlayMode run canceled at once ends canceled out of Play Mode" "canceled stopped" \
          "$(test_status_json '?detail=summary' | json_get status) $(status_field playMode)"
        "$CLI" status --until-ready --project-path "$PROJECT" --timeout 120 >/dev/null 2>&1
        # The run came across the reload into Play Mode with its tests going; cancel there stops Play Mode.
        exec_json '{"argv":["run_tests","--mode","playmode","--async-tests","true","--test-names","UpipeFixtures.Play.SlowPlay"]}' >/dev/null
        wait_test_result PlayMode
        check "play: test cancel stops a PlayMode run in Play Mode" "canceled playmode True" \
          "$(cli_result test cancel | json_eval '"%s %s %s" % (d["data"]["result"]["status"], d["data"]["result"]["mode"], 1 <= d["data"]["result"]["summary"]["total"] < 4)')"
        wait_field playMode stopped; check "play: back in Edit Mode after the cancel" 0 $?
        "$CLI" status --until-ready --project-path "$PROJECT" --timeout 120 >/dev/null 2>&1
      else
        echo "SKIP PlayMode test cancel: the host's HostFixtures copy lacks UpipeFixtures.Play.SlowPlay; copy Tests~/HostFixtures again"
      fi
      check "cli: a test run right after a cancel passes" "completed 1" \
        "$(cli_result --timeout 300 test run -- --test-names UpipeFixtures.SmokeEdit.SmokePasses | json_eval '"%s %s" % (d["data"]["result"]["status"], d["data"]["result"]["summary"]["passed"])')"
      if [ "$(echo "$LIST" | json_eval 'any(t["FullName"] == "UpipeFixtures.CleanupErrorFixture.PassesBeforeACleanupError" for t in d["data"]["result"])')" = True ]; then
        # The run starts from a saved scene of its own; the open scenes are put back and the scene deleted afterwards.
        exec_json '{"argv":["script","eval","using UnityEditor.SceneManagement; SessionState.SetString(\"UpipeSmoke.Scenes\", string.Join(\"|\", EditorSceneManager.GetSceneManagerSetup().Select(s => s.path + \"*\" + s.isLoaded + \"*\" + s.isActive))); AssetDatabase.DeleteAsset(\"Assets/UpipeSmokeScene.unity\"); return EditorSceneManager.SaveScene(EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single), \"Assets/UpipeSmokeScene.unity\");"]}' >/dev/null
        # 1.4 turns an error logged by a cleanup into a run error; the failure says what passed, which requested names
        # matched nothing (the error comes after the last RunFinished, so every result is in) and what was logged.
        "$CLI" command --project-path "$PROJECT" --timeout 300 test run -- --test-names "UpipeFixtures.CleanupErrorFixture.PassesBeforeACleanupError;UpipeFixtures.NoSuchFixture.Typo" >"$SCRIPTS/run-error.out" 2>&1; E=$?
        check "cli: a run error reports the passes, the missing names and the errors logged" "yes yes yes yes" \
          "$([ $E -ne 0 ] && echo yes) $(grep -q '1 of 1 tests passed' "$SCRIPTS/run-error.out" && echo yes) $(grep -q 'TEST_NAMES_NOT_RUN): UpipeFixtures.NoSuchFixture.Typo' "$SCRIPTS/run-error.out" && echo yes) $(grep -q 'cleanup error by design' "$SCRIPTS/run-error.out" && echo yes)"
        # Its error mode skips the framework's scene restore, as a cancel does; Upipe reopens the scene on a later update.
        check "cli: a run error reopens the scene the run started from" Assets/UpipeSmokeScene.unity \
          "$(for _ in $(seq 1 20); do A=$(exec_json '{"argv":["script","eval","return UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().path;"]}' | json_get result); [ "$A" = Assets/UpipeSmokeScene.unity ] && break; sleep 0.25; done; echo "$A")"
        exec_json '{"argv":["script","eval","using UnityEditor.SceneManagement; var setup = SessionState.GetString(\"UpipeSmoke.Scenes\", \"\").Split(new[] { \"|\" }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Split(new[] { \"*\" }, StringSplitOptions.None)).Select(p => new SceneSetup { path = p[0], isLoaded = bool.Parse(p[1]), isActive = bool.Parse(p[2]) }).ToArray(); SessionState.EraseString(\"UpipeSmoke.Scenes\"); if (setup.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(setup); else EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single); return AssetDatabase.DeleteAsset(\"Assets/UpipeSmokeScene.unity\");"]}' >/dev/null
      else
        echo "SKIP run error report: the host's HostFixtures copy lacks UpipeFixtures.CleanupErrorFixture; copy Tests~/HostFixtures again"
      fi
    else
      echo "SKIP test cancel of a running run: this project's com.unity.test-framework has no cancel API (needs 1.4.0)"
    fi
  else
    echo "SKIP test-names and test progress: the host lacks Tests~/HostFixtures"
  fi
else
  echo "SKIP test commands: the project has no com.unity.test-framework"
  check "no test framework: test list is unknown" "Command Not Found" "$(exec_json '{"argv":["test","list"]}' | json_get error)"
fi

# --- capture game_view (a hidden camera of its own, so the open scene is neither changed nor dirtied)
check "http: capture bad source" "Parameter Validation Failed" "$(exec_json '{"argv":["capture","game_view","--source","window"]}' | json_get error)"
check "http: capture save path must be .png" "Parameter Validation Failed" "$(exec_json '{"argv":["capture_game_view","--save_path","a.jpg"]}' | json_get error)"
check "http: capture negative max-resolution" "Parameter Validation Failed" "$(exec_json '{"argv":["capture","game_view","--max-resolution","-1"]}' | json_get error)"
check "http: capture width 0 refused" "Parameter Validation Failed" "$(exec_json '{"argv":["capture","game_view","--width","0"]}' | json_get error)"
check "http: capture_scene_view height 4097 refused" "Parameter Validation Failed" "$(exec_json '{"argv":["capture_scene_view","--height","4097"]}' | json_get error)"
exec_json '{"argv":["script","eval","UnityEditor.EditorUtility.CreateGameObjectWithHideFlags(\"UpipeSmokeCamera\", UnityEngine.HideFlags.HideAndDontSave, typeof(UnityEngine.Camera)); return 1;"]}' >/dev/null
CAP=$(cli_result capture game_view -- --source camera --save-path Temp/upipe-smoke/camera.png)
if echo "$CAP" | grep -q NO_GPU; then
  echo "SKIP capture: this Editor has no graphics device"
else
  check "cli: capture to a file answers path and size only" "camera 1280 720 png False" "$(echo "$CAP" | json_eval '"%s %s %s %s %s" % (d["data"]["result"]["source"], d["data"]["result"]["width"], d["data"]["result"]["height"], d["data"]["result"]["encoding"], "base64" in d["data"]["result"])')"
  check "cli: capture wrote a PNG" True "$(python3 -I -c 'import sys; print(open(sys.argv[1], "rb").read(8) == b"\x89PNG\r\n\x1a\n")' "$PROJECT/Temp/upipe-smoke/camera.png" 2>/dev/null)"
  check "cli: capture_game_view inline, scaled down" "64 36 True" "$(cli_result capture_game_view -- --source camera --max_resolution 64 | json_eval '"%s %s %s" % (d["data"]["result"]["inlineWidth"], d["data"]["result"]["inlineHeight"], len(d["data"]["result"]["base64"]) > 0)')"
  check "cli: camera capture at a custom size" "camera 320 200" "$(cli_result capture game_view -- --source camera --width 320 --height 200 | json_eval '"%s %s %s" % (d["data"]["result"]["source"], d["data"]["result"]["width"], d["data"]["result"]["height"])')"
  # A batchmode Editor has no Scene view; an interactive one shows it, as a camera render or as the window drew it.
  if [ "$(field mode)" = batchmode ]; then
    check "http: capture scene_view without a Scene view" True "$(exec_json '{"argv":["capture","scene_view"]}' | json_eval 'd["errorDetails"].startswith("NO_SCENE_VIEW")')"
  else
    check "cli: capture scene_view camera at a custom size" "camera 64 48 png True" \
      "$(cli_result capture scene_view -- --width 64 --height 48 | json_eval '"%s %s %s %s %s" % (d["data"]["result"]["source"], d["data"]["result"]["width"], d["data"]["result"]["height"], d["data"]["result"]["encoding"], len(d["data"]["result"]["base64"]) > 0)')"
    exec_json '{"argv":["script","eval","var sv = UnityEditor.SceneView.lastActiveSceneView; if (sv != null) { sv.Show(); sv.Repaint(); } return sv != null;"]}' >/dev/null
    sleep 1
    check "cli: capture_scene_view screen source" "screen True" \
      "$(cli_result capture_scene_view -- --source screen --max_resolution 256 | json_eval '"%s %s" % (d["data"]["result"]["source"], len(d["data"]["result"]["base64"]) > 0)')"
  fi
  ASSET_CAP_PREFIX=$([ -e "$PROJECT/assets" ] && echo "assets" || echo "Assets")
  check "cli: capture under Assets/ imports cleanly" True "$(cli_result capture game_view -- --source camera --save-path "$ASSET_CAP_PREFIX/UpipeSmokeTmp/cam.png" | json_eval 'd["success"]')"
  check "cli: imported capture asset has a GUID" True "$(exec_json '{"argv":["script","eval","return !string.IsNullOrEmpty(UnityEditor.AssetDatabase.AssetPathToGUID(\"Assets/UpipeSmokeTmp/cam.png\"));"]}' | json_get result)"
  exec_json '{"argv":["script","eval","return UnityEditor.AssetDatabase.DeleteAsset(\"Assets/UpipeSmokeTmp\");"]}' >/dev/null
  rm -rf "$PROJECT/Assets/UpipeSmokeTmp" "$PROJECT/Assets/UpipeSmokeTmp.meta"
fi
exec_json '{"argv":["script","eval","foreach (var c in UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.Camera>()) if (c.name == \"UpipeSmokeCamera\") UnityEngine.Object.DestroyImmediate(c.gameObject); return 1;"]}' >/dev/null

# --- a domain reload: an unfinished job reads as failed afterwards, a finished one keeps its result
J4=$(detach script run "$SCRIPTS/Tool.cs" --entry Tool.Slow --args 600000)
wait_job_state "$J4" running; check "reload: long job running" 0 $?
domain_mark() { exec_json '{"argv":["script","eval","return System.AppDomain.CurrentDomain.GetData(\"upipe-smoke\") ?? \"new\";"]}' | json_get result 2>/dev/null; }
# Raw HTTP so it is sent once. The method has run whatever the reply (a result, an error, or a connection dropped by
# the reload), so it must never be marked retryable: the CLI resends those, and each resend would reload again.
REPLY=$(exec_json '{"argv":["script","run","'"$SCRIPTS"'/ReloadNow.cs"]}')
check "reload: a script run that reloads is not retryable" 0 "$(printf '%s' "$REPLY" | grep -c '"retryable":true')"
# The old domain marks the job failed before its listener stops, so read the job only from the new domain.
sleep 1
for _ in $(seq 1 120); do [ "$(domain_mark)" = new ] && break; sleep 0.5; done
check "reload: the domain reloaded" new "$(domain_mark)"
check "reload: unfinished job reads as failed, not unknown" "failed Job Interrupted" \
  "$(job_json "$J4" | json_eval '"%s %s" % (d.get("state"), d.get("error"))')"
check "reload: finished job keeps its result" "completed 42" "$(job_json "$J1" | json_eval '"%s %s" % (d.get("state"), d.get("result"))')"
OUT=$(job_cli wait "$J4"); E=$?
check "cli: job wait reports the interrupted job" "6 True" "$E $(echo "$OUT" | json_eval '"failed: Job Interrupted" in d["errors"][0]["message"]')"
"$CLI" status --until-ready --project-path "$PROJECT" --timeout 120 >/dev/null 2>&1; check "reload: Editor back" 0 $?

# --- an eval that reloads, sent once through the CLI. The CLI resends retryable replies and each resend runs the
# script again, so count the runs in SessionState (it outlives the reload); the CLI's own reply depends on who wins
# the race with the reload, so it is not checked.
RUNS="upipe-smoke-eval-runs-$$"
"$CLI" command --project-path "$PROJECT" eval 'System.AppDomain.CurrentDomain.SetData("upipe-smoke", "old"); UnityEditor.SessionState.SetInt("'$RUNS'", UnityEditor.SessionState.GetInt("'$RUNS'", 0) + 1); UnityEditor.EditorUtility.RequestScriptReload();' >/dev/null 2>&1
sleep 1
for _ in $(seq 1 120); do [ "$(domain_mark)" = new ] && break; sleep 0.5; done
check "reload: an eval through the CLI reloaded the domain" new "$(domain_mark)"
"$CLI" status --until-ready --project-path "$PROJECT" --timeout 120 >/dev/null 2>&1; check "reload: Editor back after the eval reload" 0 $?
check "reload: the CLI ran the reloading eval once" 1 "$(exec_json '{"argv":["script","eval","return UnityEditor.SessionState.GetInt(\"'$RUNS'\", 0);"]}' | json_get result)"

# --- the same, but the eval is still awaiting when the reload lands. It has started, so the reply must not be the
# retryable 503 (the CLI would resend it and run it again from the top). It reloads on its first run only, so a
# resend shows up as a second run rather than a reload loop.
CUT="upipe-smoke-cutoff-runs-$$"
OUT=$("$CLI" command --project-path "$PROJECT" --timeout 90 eval 'System.AppDomain.CurrentDomain.SetData("upipe-smoke", "old"); var runs = UnityEditor.SessionState.GetInt("'$CUT'", 0) + 1; UnityEditor.SessionState.SetInt("'$CUT'", runs); if (runs == 1) UnityEditor.EditorUtility.RequestScriptReload(); await System.Threading.Tasks.Task.Delay(5000); return runs;' 2>&1)
check "reload: a reload cutting off an awaiting eval is reported, not resent" yes "$(printf '%s' "$OUT" | grep -q 'cut it off' && echo yes)"
sleep 1
for _ in $(seq 1 120); do [ "$(domain_mark)" = new ] && break; sleep 0.5; done
"$CLI" status --until-ready --project-path "$PROJECT" --timeout 120 >/dev/null 2>&1; check "reload: Editor back after the cut-off eval" 0 $?
check "reload: the cut-off eval ran once" 1 "$(exec_json '{"argv":["script","eval","return UnityEditor.SessionState.GetInt(\"'$CUT'\", 0);"]}' | json_get result)"
rm -rf "$SCRIPTS" "$PROJECT/Temp/upipe-smoke"

exit $FAILED
