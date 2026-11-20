#!/bin/bash
# Smoke test against a running Editor that has com.upipe.server installed.
# usage: smoke.sh [--disabled] <unity-project-path> [path-to-unity-cli]
# --disabled: the Editor was started with UPIPE_ENABLE=0 and its log already shows "[Upipe] Server disabled";
#             checks that nothing is listening for the project.
# Exits non-zero if any check fails. Never prints the token.
set -u
DISABLED=0
[ "${1:-}" = "--disabled" ] && { DISABLED=1; shift; }
PROJECT=${1:?usage: smoke.sh [--disabled] <unity-project-path> [unity-cli]}
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

# The server reports "settling" until the Editor has been idle for a moment after startup.
for _ in $(seq 1 30); do
  curl -s -H @<(auth) "$URL/api/status" | grep -q '"status":"ready"' && break
  sleep 1
done

# --- official CLI
"$CLI" command --project-path "$PROJECT" editor status >/dev/null 2>&1; check "cli: editor status" 0 $?
"$CLI" command --project-path "$PROJECT" >/dev/null 2>&1; check "cli: list groups" 0 $?
"$CLI" command --project-path "$PROJECT" editor >/dev/null 2>&1; check "cli: list editor group" 0 $?
"$CLI" list --project-path "$PROJECT" >/dev/null 2>&1; check "cli: unity list (default full catalog)" 0 $?
"$CLI" command --project-path "$PROJECT" --detail compact >/dev/null 2>&1; check "cli: compact catalog" 0 $?
"$CLI" recompile --project-path "$PROJECT" >/dev/null 2>&1; check "cli: unity recompile (no changes)" 0 $?
"$CLI" command --project-path "$PROJECT" console get --tail 5 >/dev/null 2>&1; check "cli: console get" 0 $?
check "cli: script eval" 4 "$("$CLI" command --project-path "$PROJECT" script eval --json 'return 2+2;' 2>/dev/null | json_eval 'd["data"]["result"]')"
OUT=$("$CLI" recompile --project-path "$PROJECT" --strict 2>&1); echo "$OUT" | grep -q "Couldn't read the Editor console"; check "cli: recompile --strict reads the console" 1 $?
"$CLI" command --project-path "$PROJECT" editor nope >/dev/null 2>&1; [ $? -ne 0 ]; check "cli: unknown command fails" 0 $?
# Single-token names from the unity CLI skill docs; eval's --timeout there is in ms, and the CLI's own request timeout wins.
"$CLI" command --project-path "$PROJECT" editor_status >/dev/null 2>&1; check "cli: editor_status alias" 0 $?
"$CLI" command --project-path "$PROJECT" console_status >/dev/null 2>&1; check "cli: console_status alias" 0 $?
"$CLI" command --project-path "$PROJECT" console -- --tail 2 >/dev/null 2>&1; check "cli: console alias takes flags" 0 $?
check "cli: eval alias with the skill doc's ms timeout" 1 "$("$CLI" command --json --project-path "$PROJECT" eval -- --code 'return 1;' --timeout 15000 2>/dev/null | json_eval 'd["data"]["result"]')"
OUT=$("$CLI" command --project-path "$PROJECT" editor status --bogus 2>&1); [ $? -ne 0 ]; check "cli: unknown flag fails" 0 $?

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
check "http: structured set_autotick non-boolean enable" "Parameter Validation Failed" "$(exec_json '{"command":"set_autotick","parameters":{"enable":"maybe"}}' | json_get error)"
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
"$CLI" command --project-path "$PROJECT" editor_play >/dev/null 2>&1; check "cli: editor_play alias" 0 $?
"$CLI" status --until-ready --project-path "$PROJECT" --timeout 120 >/dev/null 2>&1; check "cli: unity status --until-ready after editor_play" 0 $?
wait_field playMode playing; check "play: playing" 0 $?
[ "$HAS_TESTS" = True ] && check "play: test run refused in Play Mode" True "$(exec_json '{"argv":["test","run","--filter","upipe-smoke"]}' | json_eval 'd["errorDetails"].startswith("PLAY_MODE_ACTIVE")')"
check "play: Run In Background on while playing" True "$(status_field runInBackground)"
F1=$(status_field frameCount); sleep 1; F2=$(status_field frameCount)
[ "${F2:-0}" -gt "${F1:-0}" ] 2>/dev/null; check "play: frameCount advances" 0 $?
check "play: playerLoopTicking" True "$(status_field playerLoopTicking)"
"$CLI" command --project-path "$PROJECT" editor.playmode pause >/dev/null 2>&1; check "cli: editor.playmode pause" 0 $?
wait_field playMode paused; check "play: paused" 0 $?
check "play: playerLoopTicking false while paused" False "$(status_field playerLoopTicking)"
"$CLI" command --project-path "$PROJECT" editor.playmode pause -- --paused false >/dev/null 2>&1
wait_field playMode playing; check "play: --paused false resumes" 0 $?
"$CLI" command --project-path "$PROJECT" editor_stop >/dev/null 2>&1; check "cli: editor_stop alias" 0 $?
wait_field playMode stopped; check "play: stopped" 0 $?
"$CLI" status --until-ready --project-path "$PROJECT" --timeout 120 >/dev/null 2>&1
wait_field runInBackground "$RIB"; check "play: Run In Background back to the project's value" 0 $?
check "cli: set_autotick --enable false" False "$(cli_result set_autotick -- --enable false | json_eval 'd["data"]["result"]["enabled"]')"

# --- raw HTTP

check "http: no token -> 401" 401 "$(code "$URL/api/status")"
check "http: wrong token -> 401" 401 "$(code -H 'Authorization: Bearer wrong' "$URL/api/status")"
check "http: Origin -> 403" 403 "$(code -H @<(auth) -H 'Origin: http://evil.example' "$URL/api/status")"
check "http: Host localhost -> 400" 400 "$(code -H @<(auth) -H "Host: localhost:$PORT" "$URL/api/status")"
check "http: status ready" ready "$(curl -s -H @<(auth) "$URL/api/status" | json_get status)"
check "http: status has player-loop fields only in play mode" True "$(curl -s -H @<(auth) "$URL/api/status" | json_eval '("frameCount" in d) == ("playerLoopTicking" in d) == (d["playMode"] != "stopped")')"
check "http: editor_status needs token" 401 "$(code "$URL/api/editor_status")"
check "http: editor_status route" ready "$(curl -s -H @<(auth) "$URL/api/editor_status" | json_get status)"
check "http: editor_status fields" True "$(curl -s -H @<(auth) "$URL/api/editor_status" | json_eval 'all(k in d for k in ("domainReloadInProgress", "autoRefresh", "runInBackground", "playMode"))')"
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
check "catalog: group match includes subgroups" 6 "$(catalog '?group=editor' | json_get total)"
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
"$CLI" job status nope --project-path "$PROJECT" >/dev/null 2>&1; [ $? -ne 0 ]; check "cli: job status unknown id fails" 0 $?
check "http: job status unknown id -> 404" "404 Job Not Found" "$(code -H @<(auth) "$URL/api/job?id=nope") $(job_json nope | json_get error)"
check "http: job status needs an id" 400 "$(code -H @<(auth) "$URL/api/job")"
check "http: job status needs the token" 401 "$(code "$URL/api/job?id=$J1")"
check "http: job cancel unknown id -> 404" 404 "$(code -H @<(auth) -H 'Content-Type: application/json' -d '{"id":"nope"}' "$URL/api/job/cancel")"
check "http: job cancel needs an id" 400 "$(code -H @<(auth) -H 'Content-Type: application/json' -d '{}' "$URL/api/job/cancel")"
check "http: group listing cannot be a job" "Invalid Request" "$(exec_json '{"argv":["editor"],"job":true}' | json_get error)"
check "http: bad arguments refused at submission" INVALID_COMMAND_ARGS "$(exec_json '{"argv":["editor","status","--bogus"],"job":true}' | json_get errorCode)"
J5=$(exec_json '{"command":"editor_status","job":true}' | json_eval 'd["result"]["jobId"]')
wait_job_state "$J5" completed; check "http: structured job completes" ready "$(job_json "$J5" | json_eval 'd["result"]["status"]')"

# --- tests in this Editor (TestRunnerApi): test list / run / status / cancel, GET /api/test-status
if [ "$HAS_TESTS" = True ]; then
  test_status_json() { curl -s -H @<(auth) "$URL/api/test-status$1"; }
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
  check "http: test cancel is not supported" True "$(exec_json '{"argv":["test","cancel"]}' | json_eval 'd["errorDetails"].startswith("NOT_SUPPORTED")')"
  for _ in $(seq 1 120); do TS=$(test_status_json '?detail=summary' | json_get status 2>/dev/null); [ -n "$TS" ] && [ "$TS" != running ] && break; sleep 0.5; done
  check "play: the async playmode run reports across the reload" "completed playmode 0" "$(test_status_json '?detail=summary' | json_eval '"%s %s %s" % (d["status"], d["mode"], d["summary"]["total"])')"
  wait_field playMode stopped; check "play: back in Edit Mode after the run" 0 $?
  "$CLI" status --until-ready --project-path "$PROJECT" --timeout 120 >/dev/null 2>&1
else
  echo "SKIP test commands: the project has no com.unity.test-framework"
  check "no test framework: test list is unknown" "Command Not Found" "$(exec_json '{"argv":["test","list"]}' | json_get error)"
fi

# --- capture game_view (a hidden camera of its own, so the open scene is neither changed nor dirtied)
check "http: capture bad source" "Parameter Validation Failed" "$(exec_json '{"argv":["capture","game_view","--source","window"]}' | json_get error)"
check "http: capture save path must be .png" "Parameter Validation Failed" "$(exec_json '{"argv":["capture_game_view","--save_path","a.jpg"]}' | json_get error)"
check "http: capture negative max-resolution" "Parameter Validation Failed" "$(exec_json '{"argv":["capture","game_view","--max-resolution","-1"]}' | json_get error)"
exec_json '{"argv":["script","eval","UnityEditor.EditorUtility.CreateGameObjectWithHideFlags(\"UpipeSmokeCamera\", UnityEngine.HideFlags.HideAndDontSave, typeof(UnityEngine.Camera)); return 1;"]}' >/dev/null
CAP=$(cli_result capture game_view -- --source camera --save-path Temp/upipe-smoke/camera.png)
if echo "$CAP" | grep -q NO_GPU; then
  echo "SKIP capture: this Editor has no graphics device"
else
  check "cli: capture to a file answers path and size only" "camera 1280 720 png False" "$(echo "$CAP" | json_eval '"%s %s %s %s %s" % (d["data"]["result"]["source"], d["data"]["result"]["width"], d["data"]["result"]["height"], d["data"]["result"]["encoding"], "base64" in d["data"]["result"])')"
  check "cli: capture wrote a PNG" True "$(python3 -I -c 'import sys; print(open(sys.argv[1], "rb").read(8) == b"\x89PNG\r\n\x1a\n")' "$PROJECT/Temp/upipe-smoke/camera.png" 2>/dev/null)"
  check "cli: capture_game_view inline, scaled down" "64 36 True" "$(cli_result capture_game_view -- --source camera --max_resolution 64 | json_eval '"%s %s %s" % (d["data"]["result"]["inlineWidth"], d["data"]["result"]["inlineHeight"], len(d["data"]["result"]["base64"]) > 0)')"
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
rm -rf "$SCRIPTS" "$PROJECT/Temp/upipe-smoke"

exit $FAILED
