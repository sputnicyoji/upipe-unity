#!/usr/bin/env python3
"""Smoke test against a running Editor that has com.upipe.server installed.

usage: smoke.py [--disabled|--startup|--quit] <unity-project-path> [path-to-unity-cli]
  --disabled: the Editor was started with UPIPE_ENABLE=0 and its log already shows "[Upipe] Server disabled";
              checks that nothing is listening for the project.
  --startup:  launch the Editor first (a cold Library gives the longest settling window), then run this right away:
              it waits for the descriptor and checks that `unity status --until-ready` waits for Upipe's own ready,
              then goes on with the normal checks.
  --quit:     only checks `editor quit`, which ends the Editor: an interactive Editor first refuses with an unsaved scene,
              then --force quits; the process must exit and remove its descriptor.

The cross-platform twin of smoke.sh, with the same check names, in Python 3 standard library only (Windows and macOS).
Exits non-zero if any check fails. Never prints the token.
"""
import http.client as http_client
import json
import os
import re
import select
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

IS_WINDOWS = os.name == "nt"
sys.stdout.reconfigure(errors="backslashreplace")  # a legacy Windows console code page cannot print every character

args = sys.argv[1:]
DISABLED = bool(args) and args[0] == "--disabled"
if DISABLED:
    args = args[1:]
STARTUP = bool(args) and args[0] == "--startup"
if STARTUP:
    args = args[1:]
QUIT = bool(args) and args[0] == "--quit"
if QUIT:
    args = args[1:]
if not args:
    sys.exit("usage: smoke.py [--disabled|--startup|--quit] <unity-project-path> [unity-cli]")
PROJECT = os.path.abspath(args[0])


def default_cli():
    """Where the install script puts the CLI, like smoke.sh; PATH only as a fallback (it may hold another version)."""
    if IS_WINDOWS:
        installed = os.path.join(os.environ.get("LOCALAPPDATA", ""), "Unity", "bin", "unity.exe")
    else:
        installed = os.path.expanduser("~/.unity/bin/unity")
    return installed if os.path.isfile(installed) else shutil.which("unity") or installed


CLI = args[1] if len(args) > 1 else default_cli()
DESCRIPTOR = os.path.join(PROJECT, "Library", "Pipeline", ".unity-pipeline-port")
FAILED = 0


def check(name, expected, actual):
    global FAILED
    if expected == actual or str(expected) == str(actual):
        print(f"PASS {name}", flush=True)
    else:
        print(f"FAIL {name}: expected [{expected}] got [{actual}]", flush=True)
        FAILED = 1


def ev(fn, value):
    """Evaluates fn (written in this script, never input from the server) on a reply; a shape error becomes the actual."""
    try:
        return fn(value)
    except Exception as e:  # noqa: BLE001 - any shape problem is a check failure, reported as the actual value
        return f"<{type(e).__name__}: {e}>"


def run(argv):
    """Runs a process and returns (exit code, stdout, stdout+stderr), decoded as UTF-8 whatever the console code page."""
    p = subprocess.run(argv, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    out = p.stdout.decode("utf-8", "replace")
    return p.returncode, out, out + p.stderr.decode("utf-8", "replace")


def cli(*a):
    return run([CLI, *a])


def cli_ok(*a):
    return cli(*a)[0]


def cli_result(*a):
    """`unity command --json` and its parsed stdout (an empty dict if it is not JSON)."""
    _, out, _ = cli("command", "--json", "--project-path", PROJECT, *a)
    try:
        return json.loads(out)
    except ValueError:
        return {}


def pid_alive(pid):
    if IS_WINDOWS:
        # os.kill(pid, 0) is no probe on Windows: it calls TerminateProcess.
        import ctypes
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.OpenProcess.restype = ctypes.c_void_p
        handle = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
        if not handle:
            return ctypes.get_last_error() == 5  # access denied: it exists
        try:
            code = ctypes.c_ulong()
            return bool(kernel32.GetExitCodeProcess(ctypes.c_void_p(handle), ctypes.byref(code))) and code.value == 259
        finally:
            kernel32.CloseHandle(ctypes.c_void_p(handle))
    try:
        os.kill(pid, 0)
        return True
    except PermissionError:
        return True
    except OSError:
        return False


def field(name):
    with open(DESCRIPTOR, encoding="utf-8") as f:
        return json.load(f)[name]


if DISABLED:
    check("disabled: no descriptor", "absent", "present" if os.path.isfile(DESCRIPTOR) else "absent")
    check("disabled: cli cannot connect", True, cli_ok("command", "--project-path", PROJECT, "editor", "status") != 0)
    sys.exit(FAILED)

if STARTUP:
    # A killed batchmode Editor leaves its descriptor (and token) behind, so wait for one whose writer is alive.
    for _ in range(1200):
        try:
            if os.path.isfile(DESCRIPTOR) and pid_alive(int(field("pid"))):
                break
        except (OSError, ValueError, KeyError):
            pass
        time.sleep(0.5)
if not os.path.isfile(DESCRIPTOR):
    print(f"FAIL descriptor missing: {DESCRIPTOR}")
    sys.exit(1)
PORT = field("port")
TOKEN = field("evalToken")
URL = f"http://127.0.0.1:{PORT}"


# --- helpers (the token only ever travels in a request header)
# No proxies: urllib would send loopback requests, token included, to a Windows system or environment proxy.
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def http(path, body=None, headers=None, auth=True, method=None):
    """Returns (status, body text); a refused connection is status 0."""
    h = dict(headers or {})
    if auth:
        h["Authorization"] = f"Bearer {TOKEN}"
    data = None
    if body is not None:
        data = (body if isinstance(body, str) else json.dumps(body)).encode("utf-8")
        h.setdefault("Content-Type", "application/json")
    req = urllib.request.Request(URL + path, data=data, headers=h, method=method)
    try:
        with OPENER.open(req, timeout=600) as r:
            return r.status, r.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")
    except (urllib.error.URLError, ConnectionError, TimeoutError) as e:
        return 0, str(e)


def code(path, **kw):
    return http(path, **kw)[0]


def as_json(text):
    try:
        return json.loads(text)
    except ValueError:
        return {}


def get_json(path, **kw):
    return as_json(http(path, **kw)[1])


def exec_raw(payload):
    return http("/api/exec", body=payload)[1]


def exec_json(payload):
    return as_json(exec_raw(payload))


def jget(d, key):
    return d.get(key) if isinstance(d, dict) else None


def owner_only():
    """'600' when only the owner can read the descriptor, otherwise what the file system reports."""
    if not IS_WINDOWS:
        return format(os.stat(DESCRIPTOR).st_mode & 0o777, "o")
    # The NTFS equivalent of 0600: a protected DACL with one full-control entry for the current user.
    ps = ("$a = Get-Acl -LiteralPath $env:UPIPE_SMOKE_DESCRIPTOR; $a.GetSecurityDescriptorSddlForm('Access'); "
          "[Security.Principal.WindowsIdentity]::GetCurrent().User.Value")
    # Windows PowerShell cannot load Get-Acl with the PSModulePath that PowerShell 7 passes down, so leave it out.
    env = {k: v for k, v in os.environ.items() if k.upper() != "PSMODULEPATH"}
    env["UPIPE_SMOKE_DESCRIPTOR"] = DESCRIPTOR
    try:
        p = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", ps], stdout=subprocess.PIPE,
                           stderr=subprocess.PIPE, env=env)
    except OSError:
        return "unreadable ACL"
    lines = p.stdout.decode("utf-8", "replace").split()
    if len(lines) != 2:
        return "unreadable ACL"
    sddl, sid = lines
    if re.fullmatch(rf"D:P(AI)?\(A;;FA;;;{re.escape(sid)}\)", sddl):
        return "600"
    return re.sub(r"S-1-5-21-[0-9-]+", "<sid>", sddl)


def result_of(d):
    """The command result in a `unity command --json` reply."""
    return d["data"]["result"]


check("descriptor is owner-only", "600", owner_only())

if QUIT:
    editor_pid = int(field("pid"))
    if field("mode") == "editor":
        # An interactive Editor must not drop unsaved work unless told to.
        exec_raw({"argv": ["script", "eval",
                           'var go = new UnityEngine.GameObject("UpipeSmokeDirty"); '
                           'UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene); return 1;']})
        check("quit: unsaved scene refused", True,
              ev(lambda d: d["errorDetails"].startswith("UNSAVED_SCENES"), exec_json({"argv": ["editor", "quit"]})))
        check("quit: --confirm false still refused", True,
              ev(lambda d: d["errorDetails"].startswith("UNSAVED_SCENES"),
                 exec_json({"argv": ["editor_quit", "--confirm", "false"]})))
    check("quit: bad exit-code refused", "INVALID_COMMAND_ARGS",
          jget(exec_json({"argv": ["editor", "quit", "--exit-code", "x"]}), "errorCode"))
    check("cli: editor_quit --force answers before quitting", "True 0",
          ev(lambda d: "%s %s" % (result_of(d)["quitting"], result_of(d)["exitCode"]),
             cli_result("editor_quit", "--", "--force")))
    for _ in range(120):
        if not pid_alive(editor_pid):
            break
        time.sleep(0.5)
    check("quit: the Editor process exited", "gone", "alive" if pid_alive(editor_pid) else "gone")
    check("quit: descriptor removed", "absent", "present" if os.path.isfile(DESCRIPTOR) else "absent")
    sys.exit(FAILED)

# The CLI calls an Editor ready when GET /api/commands answers 2xx and `starting` when it answers 503; it never reads
# the body of /api/status. So while Upipe is settling the catalog must be 503, and `status --until-ready` must not
# return before Upipe reports ready. The settling window may already be over when this starts (then only the end
# state is checked); a catalog that answered 200 must never be followed by a status of settling, since settled is one-way.
if STARTUP:
    early_catalog = code("/api/commands")
    early_status = jget(get_json("/api/status"), "status")
    print(f"NOTE startup: first probe saw /api/commands {early_catalog}, /api/status {early_status}")
    if early_catalog == 200:
        check("startup: catalog 200 implies status ready", "ready", early_status)
    if early_status == "settling":
        check("startup: status settling implies catalog 503", 503, early_catalog)
    check("startup: status --until-ready returns", 0,
          cli_ok("status", "--until-ready", "--timeout", "900", "--project-path", PROJECT))
    check("startup: status is ready once --until-ready returned", "ready", jget(get_json("/api/status"), "status"))
    check("startup: catalog is 200 once --until-ready returned", 200, code("/api/commands"))

# The server reports "settling" until the Editor has been idle for a moment after startup.
for _ in range(30):
    if jget(get_json("/api/status"), "status") == "ready":
        break
    time.sleep(1)


def eval_result(source):
    return jget(exec_json({"argv": ["script", "eval", source]}), "result")


# --- official CLI
exec_raw({"argv": ["script", "eval", 'System.AppDomain.CurrentDomain.SetData("upipe-noop-recompile", "kept"); return 1;']})
check("cli: editor status", 0, cli_ok("command", "--project-path", PROJECT, "editor", "status"))
check("cli: list groups", 0, cli_ok("command", "--project-path", PROJECT))
check("cli: list editor group", 0, cli_ok("command", "--project-path", PROJECT, "editor"))
check("cli: unity list (default full catalog)", 0, cli_ok("list", "--project-path", PROJECT))
check("cli: compact catalog", 0, cli_ok("command", "--project-path", PROJECT, "--detail", "compact"))
check("cli: unity recompile (no changes)", 0, cli_ok("recompile", "--project-path", PROJECT))
check("cli: no-change recompile skipped domain reload", "kept",
      eval_result('return System.AppDomain.CurrentDomain.GetData("upipe-noop-recompile");'))
check("cli: console get", 0, cli_ok("command", "--project-path", PROJECT, "console", "get", "--tail", "5"))
_, out, _ = cli("command", "--project-path", PROJECT, "script", "eval", "--json", "return 2+2;")
check("cli: script eval", 4, ev(result_of, as_json(out)))
_, out, _ = cli("command", "--project-path", PROJECT, "script", "eval", "--json",
                '#if UNITY_EDITOR\nreturn "编辑_αβ";\n#else\nreturn "player";\n#endif')
check("cli: script eval honors UNITY_EDITOR define and UTF-8", "编辑_αβ", ev(result_of, as_json(out)))
rc, _, both = cli("recompile", "--project-path", PROJECT, "--strict")
check("cli: recompile --strict reads the console", "ok",
      "ok" if (rc == 0 or "warning" in both.lower()) and "Couldn't read the Editor console" not in both else "")
check("cli: unknown command fails", "yes", "yes" if "Command Not Found" in cli("command", "--project-path", PROJECT, "editor", "nope")[2] else "")
# Single-token names from the unity CLI skill docs; eval's --timeout there is in ms, and the CLI's own request timeout wins.
check("cli: editor_status alias", 0, cli_ok("command", "--project-path", PROJECT, "editor_status"))
check("cli: console_status alias", 0, cli_ok("command", "--project-path", PROJECT, "console_status"))
check("cli: console alias takes flags", 0, cli_ok("command", "--project-path", PROJECT, "console", "--", "--tail", "2"))
check("cli: eval alias with the skill doc's ms timeout", 1,
      ev(result_of, cli_result("eval", "--", "--code", "return 1;", "--timeout", "15000")))
check("cli: unknown flag fails", "yes", "yes" if "no parameter --bogus" in cli("command", "--project-path", PROJECT, "editor", "status", "--bogus")[2] else "")

# The test commands exist only when the project has com.unity.test-framework.
HAS_TESTS = jget(exec_json({"argv": ["test", "list", "--mode", "editor"]}), "success") is True


# --- auto tick, Run In Background, console clear/write, play mode; leaves auto tick off and Run In Background as found
def status_field(name):
    return str(jget(get_json("/api/editor_status"), name))


def wait_field(name, value):
    """Polls /api/editor_status, which stays answerable except during a reload; 0 once it matches."""
    for _ in range(60):
        if status_field(name) == str(value):
            return 0
        time.sleep(1)
    return 1


def until_ready(timeout="120"):
    return cli_ok("status", "--until-ready", "--project-path", PROJECT, "--timeout", timeout)


RIB = status_field("runInBackground")
check("cli: set_autotick alias", "True 16 True",
      ev(lambda d: "%s %s %s" % (result_of(d)["enabled"], result_of(d)["intervalMs"], result_of(d)["persist"]),
         cli_result("set_autotick")))
check("cli: set_autotick snake_case flags", "32 False",
      ev(lambda d: "%s %s" % (result_of(d)["intervalMs"], result_of(d)["persist"]),
         cli_result("set_autotick", "--", "--interval_ms", "32", "--persist", "false")))
check("http: set_auto_tick interval-ms 0 rejected", "Parameter Validation Failed",
      jget(exec_json({"argv": ["editor", "set_auto_tick", "--interval-ms", "0"]}), "error"))
check("http: structured set_autotick non-boolean enable", "Invalid Command Arguments",
      jget(exec_json({"command": "set_autotick", "parameters": {"enable": "maybe"}}), "error"))
check('http: structured eval dry-run "1" is refused, not run', "Invalid Command Arguments",
      jget(exec_json({"command": "eval", "parameters": {"code": "return 1;", "dry-run": "1"}}), "error"))
check("cli: editor_set_run_in_background", "True True",
      ev(lambda d: "%s %s" % (result_of(d)["runInBackground"], result_of(d)["overridden"]),
         cli_result("editor_set_run_in_background")))
check("cli: editor_set_run_in_background --enable false restores", f"{RIB} False",
      ev(lambda d: "%s %s" % (result_of(d)["runInBackground"], result_of(d)["overridden"]),
         cli_result("editor_set_run_in_background", "--", "--enable", "false")))
TAG = f"upipe-smoke-{os.getpid()}"
exec_raw({"argv": ["console", "write", f"{TAG}-before"]})
check("cli: clear_console alias", 0, cli_ok("command", "--project-path", PROJECT, "clear_console"))
check("cli: console write", 0,
      cli_ok("command", "--project-path", PROJECT, "console", "write", "--", "--message", f"{TAG}-after", "--level", "warn"))
check("http: console clear keeps only later entries", [(f"{TAG}-after", "warn")],
      ev(lambda d: [(e["message"], e["level"]) for e in d["result"]["entries"] if e["message"].startswith(TAG)],
         exec_json({"argv": ["console", "get", "--tail", "2000"]})))


def gt_warnings():
    return ev(lambda d: d["result"]["groundTruth"]["consoleWarnings"], exec_json({"command": "console_status"}))


for _ in range(5):  # ground truth is sampled every 0.25 s
    if gt_warnings() != 0:
        break
    time.sleep(0.5)
check("http: Editor console counted the written warning", True, ev(lambda w: w >= 1, gt_warnings()))
check("http: structured clear_console", True, jget(exec_json({"command": "clear_console"}), "success"))
check("http: clear_console empties the Editor console too", 0, gt_warnings())
check("http: console write bad level", "Parameter Validation Failed",
      jget(exec_json({"command": "console write", "parameters": {"message": "x", "level": "info"}}), "error"))
check("http: console write needs message", "INVALID_COMMAND_ARGS",
      jget(exec_json({"argv": ["console", "write"]}), "errorCode"))
check("cli: console_write alias", 0,
      cli_ok("command", "--project-path", PROJECT, "console_write", "--", "--message", f"upipe-alias-{os.getpid()}"))
check("cli: editor_play alias", 0, cli_ok("command", "--project-path", PROJECT, "editor_play"))
check("cli: unity status --until-ready after editor_play", 0, until_ready())
check("play: playing", 0, wait_field("playMode", "playing"))
if HAS_TESTS:
    check("play: test run refused in Play Mode", True,
          ev(lambda d: d["errorDetails"].startswith("PLAY_MODE_ACTIVE"),
             exec_json({"argv": ["test", "run", "--filter", "upipe-smoke"]})))
check("play: Run In Background on while playing", "True", status_field("runInBackground"))
F1 = status_field("frameCount")
time.sleep(1)
F2 = status_field("frameCount")
check("play: frameCount advances", True, ev(lambda f: int(f[1]) > int(f[0]), (F1, F2)))
check("play: playerLoopTicking", "True", status_field("playerLoopTicking"))
check("cli: editor_pause alias", 0, cli_ok("command", "--project-path", PROJECT, "editor_pause"))
check("play: paused", 0, wait_field("playMode", "paused"))
check("play: playerLoopTicking false while paused", "False", status_field("playerLoopTicking"))
cli("command", "--project-path", PROJECT, "editor.playmode", "pause", "--", "--paused", "false")
check("play: --paused false resumes", 0, wait_field("playMode", "playing"))
check("cli: editor_stop alias", 0, cli_ok("command", "--project-path", PROJECT, "editor_stop"))
check("play: stopped", 0, wait_field("playMode", "stopped"))
until_ready()
check("play: Run In Background back to the project's value", 0, wait_field("runInBackground", RIB))
check("cli: set_autotick --enable false", False,
      ev(lambda d: result_of(d)["enabled"], cli_result("set_autotick", "--", "--enable", "false")))
exec_raw({"argv": ["console", "write", f"{TAG}-after-play"]})
check("http: console capture survives leaving Play Mode", [f"{TAG}-after-play"],
      ev(lambda d: [e["message"] for e in d["result"]["entries"] if e["message"] == f"{TAG}-after-play"],
         exec_json({"argv": ["console", "get", "--tail", "50"]})))

# --- raw HTTP
check("http: no token -> 401", 401, code("/api/status", auth=False))
check("http: wrong token -> 401", 401, code("/api/status", auth=False, headers={"Authorization": "Bearer wrong"}))
check("http: Origin -> 403", 403, code("/api/status", headers={"Origin": "http://evil.example"}))
# The CLI switches Host to localhost:<port> after a refused connection during a reload; any other Host stays refused.
check("http: Host localhost -> 200", 200, code("/api/status", headers={"Host": f"localhost:{PORT}"}))
check("http: other Host -> 400", 400, code("/api/status", headers={"Host": f"rebind.example:{PORT}"}))
check("http: status ready", "ready", jget(get_json("/api/status"), "status"))
check("http: status reports non-negative lastTickAgeMs", True,
      ev(lambda d: isinstance(d.get("lastTickAgeMs"), int) and d["lastTickAgeMs"] >= 0, get_json("/api/status")))
check("http: status has player-loop fields only in play mode", True,
      ev(lambda d: ("frameCount" in d) == ("playerLoopTicking" in d) == (d["playMode"] != "stopped"), get_json("/api/status")))
check("http: editor_status needs token", 401, code("/api/editor_status", auth=False))
check("http: editor_status route", "ready", jget(get_json("/api/editor_status"), "status"))
with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "package.json"), encoding="utf-8") as f:
    PACKAGE_VERSION = json.load(f)["version"]
check("http: editor_status reports the package version", f"Upipe {PACKAGE_VERSION}", jget(get_json("/api/editor_status"), "server"))
check("http: editor_status fields", True,
      ev(lambda d: all(k in d for k in ("domainReloadInProgress", "autoRefresh", "runInBackground", "playMode", "lastTickAgeMs")),
         get_json("/api/editor_status")))
check("http: argv editor_status alias", True, jget(exec_json({"argv": ["editor_status"]}), "success"))
check("http: argv console_status alias", True, jget(exec_json({"argv": ["console_status"]}), "success"))
check("http: structured eval alias", 2, jget(exec_json({"command": "eval", "parameters": {"code": "1 + 1"}}), "result"))
check("http: argv --timeout_ms binds", 3,
      jget(exec_json({"argv": ["eval", "--code", "1 + 2", "--timeout_ms", "20000"]}), "result"))
check("http: timeout-ms applies without a request timeout", "TIMEOUT",
      jget(exec_json({"argv": ["eval", "--code", "return 1;", "--timeout-ms", "1"]}), "errorCode"))
check("http: structured full name", True, jget(exec_json({"command": "editor status"}), "success"))
check("http: structured bare name + token", True,
      jget(exec_json({"command": "recompile_status", "parameters": {"token": "x"}}), "success"))
check("http: shared bare name is ambiguous", "Command Not Found", jget(exec_json({"command": "status"}), "error"))
check("http: console_status alias", True,
      jget(exec_json({"command": "console_status", "parameters": {"token": "x"}}), "success"))
check("http: console get shape", ['counts', 'cursor', 'dropped', 'entries', 'groundTruth', 'reset', 'returned', 'session'],
      ev(lambda d: sorted(d["result"].keys()), exec_json({"argv": ["console", "get", "--tail", "2"]})))
check("http: console get bad tail", "INVALID_COMMAND_ARGS",
      jget(exec_json({"argv": ["console", "get", "--tail", "x"]}), "errorCode"))
check("http: eval trailing expression", 6,
      jget(exec_json({"command": "script eval", "parameters": {"code": "var x = 3; x * 2"}}), "result"))
check("http: eval compile error", "COMPILATION_ERROR: Compilation Failed: 1 error(s)",
      ev(lambda d: d["errorDetails"].split("\n")[0], exec_json({"argv": ["script", "eval", "return nope;"]})))
check("http: eval runtime error", True,
      ev(lambda d: d["errorDetails"].startswith("RUNTIME_EXCEPTION"),
         exec_json({"argv": ["script", "eval", 'throw new Exception("x");']})))
check("http: eval needs code", "Parameter Validation Failed", jget(exec_json({"argv": ["script", "eval"]}), "error"))
check("http: structured dotted name", True, jget(exec_json({"command": "editor.status"}), "success"))
check("http: structured unknown key", "INVALID_COMMAND_ARGS",
      jget(exec_json({"command": "editor status", "parameters": {"nope": 1}}), "errorCode"))
check("http: argv + parameters rejected", "Invalid Request",
      jget(exec_json({"argv": ["editor", "status"], "parameters": {}}), "error"))
def post_too_large(chunked=False):
    """(status, Connection header) of a POST over 4 MB; status 0 when no reply could be read."""
    total = 4 * 1024 * 1024 + 1
    conn = http_client.HTTPConnection("127.0.0.1", PORT, timeout=30)
    try:
        conn.putrequest("POST", "/api/exec")
        conn.putheader("Authorization", f"Bearer {TOKEN}")
        conn.putheader("Content-Type", "application/json")
        if chunked:
            conn.putheader("Transfer-Encoding", "chunked")
        else:
            conn.putheader("Content-Length", str(total))
        conn.endheaders()

        def reply():
            try:
                response = conn.getresponse()
                return response.status, (response.getheader("Connection") or "").lower()
            except (http_client.HTTPException, OSError):
                return 0, ""

        # On Windows, streaming body chunks after the server already rejected Content-Length triggers a TCP RST
        # that discards the unread 413 response; check for an early reply before writing the body.
        if not chunked and conn.sock and select.select([conn.sock], [], [], 0.5)[0]:
            return reply()
        chunk = b"a" * 65536
        sent = 0
        try:
            while sent < total:
                if conn.sock and select.select([conn.sock], [], [], 0)[0]:
                    break
                piece = chunk if total - sent >= len(chunk) else b"a" * (total - sent)
                if chunked:
                    conn.send(f"{len(piece):x}\r\n".encode("ascii") + piece + b"\r\n")
                else:
                    conn.send(piece)
                sent += len(piece)
            if chunked and sent >= total:
                conn.send(b"0\r\n\r\n")
        except OSError:
            pass
        return reply()
    finally:
        conn.close()


check("http: bad json", "Invalid JSON", jget(exec_json("{oops"), "error"))
TOO_LARGE = post_too_large(chunked=False)
check("http: body over 4 MB -> 413", 413, TOO_LARGE[0])
check("http: a 413 reply closes the connection", "close", post_too_large(chunked=False)[1])
check("http: chunked body over 4 MB -> 413", 413, post_too_large(chunked=True)[0])
check("http: huge timeout rejected cleanly", "Invalid Request",
      jget(exec_json('{"command":"editor status","timeout":99999999999999999999}'), "error"))
check("http: not found echoes typed name", "nosuch", jget(exec_json({"argv": ["nosuch", "--x"]}), "command"))
check("http: --= is emptyName", "emptyName",
      ev(lambda d: d["argProblems"][0]["kind"], exec_json({"argv": ["editor", "status", "--="]})))
check("http: structured not found omits command", None, jget(exec_json({"command": "nosuch"}), "command"))
check("http: recompile_status shape", ['compilationFailed', 'errors', 'failed', 'status'],
      ev(lambda d: sorted(d["result"].keys()), exec_json({"command": "recompile_status", "parameters": {"token": "x"}})))


# --- /api/commands
def catalog(query):
    return get_json("/api/commands" + query)


check("catalog: default detail is full", True,
      ev(lambda d: "schema" in d["commands"][0] and "parameters" in d["commands"][0], catalog("")))
check("catalog: compact drops schema", False, ev(lambda d: "schema" in d["commands"][0], catalog("?detail=compact")))
check("catalog: bad detail -> 400", 400, code("/api/commands?detail=bogus"))
check("catalog: bad offset -> 400", 400, code("/api/commands?offset=-1"))
check("catalog: group is segment-aware", 0, jget(catalog("?group=edit"), "total"))
check("catalog: group match includes subgroups", 7, jget(catalog("?group=editor"), "total"))
check("catalog: capture group has game_view and scene_view", 2, jget(catalog("?group=capture"), "total"))
check("catalog: query is case-insensitive", 2, jget(catalog("?query=READY"), "total"))
check("catalog: names are unique (unity mcp tool names)", True,
      ev(lambda d: len({c["name"] for c in d["commands"]}) == len(d["commands"]), catalog("")))
check("catalog: query matches the alias", 1, jget(catalog("?query=console_status"), "total"))
check("catalog: groups index", True, ev(lambda d: "editor" in [g["group"] for g in d["groups"]], catalog("?detail=groups")))

# --- script run: a .cs file outside the project, compiled without an import or a domain reload
SCRIPTS = tempfile.mkdtemp(prefix="upipe-smoke-")
TOOL = os.path.join(SCRIPTS, "Tool.cs")
BROKEN = os.path.join(SCRIPTS, "Broken.cs")
RELOAD_NOW = os.path.join(SCRIPTS, "ReloadNow.cs")


def write(path, text):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


write(TOOL, """using System.Threading;
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
""")
write(BROKEN, "public static class Broken { public static int Run() => nope; }\n")
write(RELOAD_NOW, """public static class ReloadNow
{
    public static string Run()
    {
        System.AppDomain.CurrentDomain.SetData("upipe-smoke", "old"); // gone in the next domain
        UnityEditor.EditorUtility.RequestScriptReload();
        return "requested";
    }
}
""")
IN_PROJECT = os.path.join(PROJECT, "Temp", "upipe-smoke")
os.makedirs(IN_PROJECT, exist_ok=True)
shutil.copy(TOOL, os.path.join(IN_PROJECT, "Tool.cs"))
check("cli: script run sync entry with JSON args", 5,
      ev(result_of, cli_result("script", "run", TOOL, "--entry", "Tool.Add", "--args", "[2,3]")))
check("cli: script_run alias awaits Task<int>", 30,
      ev(result_of, cli_result("script_run", "--", TOOL, "--entry", "Tool.Slow", "--args", "30")))
check("cli: script run --defines plus the Editor's defines", "both",
      ev(result_of, cli_result("script", "run", TOOL, "--entry", "Tool.Defined", "--defines", "UPIPE_SMOKE;OTHER")))
check("http: script run Windows-style relative path", 2,
      jget(exec_json({"argv": ["script", "run", "Temp\\upipe-smoke\\Tool.cs", "--entry", "Tool.Add", "--args", "[1,1]"]}), "result"))
check("http: structured script run with JSON args", 7,
      jget(exec_json({"command": "script run", "parameters": {"file": TOOL, "entry": "Tool.Add", "args": {"a": 3, "b": 4}}}), "result"))
check("http: script run compile error", "COMPILATION_ERROR: Compilation Failed: 1 error(s)",
      ev(lambda d: d["errorDetails"].split("\n")[0], exec_json({"argv": ["script", "run", BROKEN]})))
check("http: script run missing file", "Parameter Validation Failed",
      jget(exec_json({"argv": ["script", "run", os.path.join(SCRIPTS, "nope.cs")]}), "error"))
check("http: script run needs a file", "INVALID_COMMAND_ARGS", jget(exec_json({"argv": ["script", "run"]}), "errorCode"))

# --- script run with --extra-files (files and directories) and a legacy instance entry
LIB = os.path.join(SCRIPTS, "lib")
SUB = os.path.join(LIB, "sub")
HIDDEN = os.path.join(LIB, ".hidden")
os.makedirs(SUB, exist_ok=True)
os.makedirs(HIDDEN, exist_ok=True)
MULTI = os.path.join(SCRIPTS, "Multi.cs")
HELPERS = os.path.join(LIB, "Helpers.cs")
DEEP = os.path.join(SUB, "Deep.cs")
BAD_HELPER = os.path.join(SCRIPTS, "BadHelper.cs")
LEGACY = os.path.join(SCRIPTS, "Legacy.cs")
BEHAVIOUR = os.path.join(SCRIPTS, "Behaviour.cs")
DEFINE_FIRST = os.path.join(SCRIPTS, "DefineFirst.cs")

write(MULTI, "public static class Multi { public static int Run() => Helpers.Twice(Deep.Value); }\n")
write(HELPERS, 'public static class Helpers { public static int Twice(int x) => 2 * x; public static string Run() => "helper"; }\n')
write(DEEP, "public static class Deep { public const int Value = 21; }\n")
write(os.path.join(HIDDEN, "Broken.cs"), "this is not C#\n")
write(BAD_HELPER, "public static class BadHelper\n{\n    public static int Value => nope;\n}\n")
write(LEGACY, """public interface IEntrance { void Main(); }
public class LegacyEntrance : IEntrance
{
    public void Main() => UnityEngine.Debug.Log("upipe-smoke-legacy-main");
}
""")
write(BEHAVIOUR, "public class Behaviour : UnityEngine.MonoBehaviour { public void Main() { } }\n")
write(DEFINE_FIRST, """#define UPIPE_SMOKE_FIRST
public static class DefineFirst { public static string Run() =>
#if UPIPE_SMOKE_FIRST
 "defined";
#else
 "missing";
#endif
}
""")

check("cli: script run --extra-files directory, default entry from the main file", 42,
      ev(result_of, cli_result("script", "run", MULTI, "--extra-files", f"{LIB};{MULTI}")))
check("cli: script run --entry in an extra file", "helper",
      ev(result_of, cli_result("script", "run", MULTI, "--extra-files", LIB, "--entry", "Helpers.Run")))
check("http: argv repeats --extra-files", 42,
      jget(exec_json({"argv": ["script", "run", MULTI, "--extra-files", HELPERS, "--extra-files", SUB]}), "result"))
check("http: structured extra_files array", 42,
      jget(exec_json({"command": "script_run", "parameters": {"file": MULTI, "extra_files": [LIB]}}), "result"))
check("http: an error in an extra file names it", True,
      ev(lambda d: d["errorDetails"].split("\n")[1].startswith("BadHelper.cs: line 3, col 31: error CS0103"),
         exec_json({"argv": ["script", "run", TOOL, "--entry", "Tool.Add", "--args", "[1,1]", "--extra-files", BAD_HELPER]})))
check("http: script run missing extra file", "Parameter Validation Failed",
      jget(exec_json({"argv": ["script", "run", MULTI, "--extra-files", os.path.join(SCRIPTS, "nope.cs")]}), "error"))
check("cli: legacy instance Main runs on a new instance", True,
      ev(lambda d: d["success"], cli_result("script", "run", LEGACY)))
check("http: the legacy Main logged", True,
      ev(lambda d: any(e["message"] == "upipe-smoke-legacy-main" for e in d["result"]["entries"]),
         exec_json({"argv": ["console", "get", "--tail", "50"]})))
check("http: instance entry on a MonoBehaviour refused", "Parameter Validation Failed",
      jget(exec_json({"argv": ["script", "run", BEHAVIOUR]}), "error"))
check("http: script run keeps a #define on the first line", "defined",
      jget(exec_json({"argv": ["script", "run", DEFINE_FIRST, "--extra-files", LIB]}), "result"))


# --- detached jobs: unity command --detach, unity job status / wait / cancel
def job_json(job_id):
    return get_json(f"/api/job?id={job_id}")


def job_state(job_id):
    return jget(job_json(job_id), "state")


def wait_job_state(job_id, state):
    for _ in range(120):
        if job_state(job_id) == state:
            return 0
        time.sleep(0.5)
    return 1


def detach(*a):
    return ev(lambda d: d["data"]["jobId"], cli_result(*a, "--detach"))


def job_cli(sub, job_id):
    rc, out, _ = cli("job", sub, job_id, "--project-path", PROJECT, "--format", "json")
    return rc, as_json(out)


J1 = detach("script", "eval", "return 6*7;")
check("cli: --detach returns a job id", True, isinstance(J1, str) and J1 != "" and not J1.startswith("<"))
check("cli: job wait prints the result", "completed 42",
      ev(lambda d: "%s %s" % (d["data"]["state"], d["data"]["result"]), job_cli("wait", J1)[1]))
check("cli: job status", "completed script eval False",
      ev(lambda d: "%s %s %s" % (d["data"]["state"], d["data"]["command"], d["data"]["cancellationRequested"]),
         job_cli("status", J1)[1]))
J2 = detach("script", "run", TOOL, "--entry", "Tool.Slow", "--args", "600000")
check("job: long script run is running", 0, wait_job_state(J2, "running"))
check("cli: job cancel requests cancellation", True, ev(lambda d: d["data"]["cancellationRequested"], job_cli("cancel", J2)[1]))
rc, out = job_cli("wait", J2)
check("cli: job wait on a cancelled job fails", "6 True", f"{rc} {ev(lambda d: 'was canceled' in d['errors'][0]['message'], out)}")
check("cli: job status canceled", "canceled", ev(lambda d: d["data"]["state"], job_cli("status", J2)[1]))
J3 = detach("script", "run", TOOL, "--entry", "Tool.Slow", "--args", "600000", "--timeout-ms", "300")
rc, out = job_cli("wait", J3)
check("cli: --timeout-ms bounds a detached job", "6 True",
      f"{rc} {ev(lambda d: 'failed: Request Timed Out' in d['errors'][0]['message'], out)}")
check("cli: job status unknown id fails", "yes",
      "yes" if "Job Not Found" in cli("job", "status", "nope", "--project-path", PROJECT)[2] else "")
check("http: job status unknown id -> 404", "404 Job Not Found", f"{code('/api/job?id=nope')} {jget(job_json('nope'), 'error')}")
check("http: job status needs an id", 400, code("/api/job"))
check("http: job status needs the token", 401, code(f"/api/job?id={J1}", auth=False))
check("http: job cancel unknown id -> 404", 404, code("/api/job/cancel", body={"id": "nope"}))
check("http: job cancel needs an id", 400, code("/api/job/cancel", body={}))
check("http: group listing cannot be a job", "Invalid Request", jget(exec_json({"argv": ["editor"], "job": True}), "error"))
check("http: bad arguments refused at submission", "INVALID_COMMAND_ARGS",
      jget(exec_json({"argv": ["editor", "status", "--bogus"], "job": True}), "errorCode"))
J5 = ev(lambda d: d["result"]["jobId"], exec_json({"command": "editor_status", "job": True}))
wait_job_state(J5, "completed")
check("http: structured job completes", "ready", ev(lambda d: d["result"]["status"], job_json(J5)))

# A script reports progress through a standard library parameter; job status shows it while running, never after.
PROGRESS_CS = os.path.join(SCRIPTS, "Progress.cs")
GO_FILE = os.path.join(SCRIPTS, "go")
write(PROGRESS_CS, """using System;
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
""")
J6 = detach("script", "run", PROGRESS_CS, "--args", json.dumps(GO_FILE))
for _ in range(120):
    if ev(lambda d: d.get("progress") and d["progress"]["message"], job_json(J6)) == "halfway":
        break
    time.sleep(0.5)
check("http: job reports progress while running", "running halfway 50",
      ev(lambda d: "%s %s %s" % (d["state"], d["progress"]["message"], d["progress"]["percent"]), job_json(J6)))
check("cli: job status shows progress", "halfway 50",
      ev(lambda d: "%s %s" % (d["data"]["progress"]["message"], d["data"]["progress"]["percent"]), job_cli("status", J6)[1]))
write(GO_FILE, "")
check("cli: job wait after progress", "completed done",
      ev(lambda d: "%s %s" % (d["data"]["state"], d["data"]["result"]), job_cli("wait", J6)[1]))
check("http: finished job has no progress", False, ev(lambda d: "progress" in d, job_json(J6)))

# --- tests in this Editor (TestRunnerApi): test list / run / status / cancel, GET /api/test-status
if HAS_TESTS:
    check("http: capabilities include tests.camelCase", True,
          ev(lambda d: "tests.camelCase" in d.get("capabilities", []), get_json("/api/status")))

    def test_status_json(query):
        return get_json("/api/test-status" + query)

    def wait_test_run():
        for _ in range(120):
            status = jget(test_status_json("?detail=summary"), "status")
            if status and status != "running":
                return
            time.sleep(0.5)

    def wait_test_result(phase="EditMode"):
        """Polls until the running run has at least one result, in the given phase."""
        for _ in range(120):
            d = test_status_json("?detail=summary")
            if isinstance(d, dict) and d.get("status") == "running" and d.get("phase") == phase and d.get("summary", {}).get("total", 0) >= 1:
                return
            time.sleep(0.25)

    # test cancel is compiled in when Upipe's asmdef defines UPIPE_TEST_CANCEL (test-framework 1.4.0 or newer). Asking the server
    # instead of reading packages-lock.json reads a pre-release, local or embedded test-framework the way the asmdef does.
    CANCEL_API = eval_result('return System.Type.GetType("Upipe.Editor.TestCommands, Upipe.Editor")?.GetMethod("CancelFrameworkJob", '
                             'System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static) != null;') is True

    LIST = cli_result("test", "list")
    check("cli: test list records", True,
          ev(lambda d: all(set(t) == {"FullName", "Mode", "Assembly", "Categories", "Explicit"} for t in result_of(d)), LIST))
    OWN = ev(lambda d: sum(t["FullName"].startswith("Upipe.Editor.Tests.TestCommandsTests.") for t in result_of(d)), LIST)
    if isinstance(OWN, int) and OWN > 0:
        check("cli: test run waits for an EditMode run", f"completed editor {OWN} {OWN} 0",
              ev(lambda d: "%s %s %s %s %s" % (result_of(d)["status"], result_of(d)["mode"], result_of(d)["summary"]["total"],
                                               result_of(d)["summary"]["passed"], result_of(d)["summary"]["failed"]),
                 cli_result("--timeout", "300", "test", "run", "--", "--filter", "Upipe.Editor.Tests.TestCommandsTests")))
        _, out, _ = cli("command", "--project-path", PROJECT, "--timeout", "300", "run_tests", "--", "--filter", "testcommandstests")
        check("cli: run_tests summary line", f"{OWN}/{OWN} passed (editor)", ev(lambda o: o.strip().splitlines()[-1].split("\t")[2], out))
        _, out, _ = cli("command", "--project-path", PROJECT, "test_status")
        check("cli: test_status summary line", f"completed: {OWN}/{OWN} passed",
              ev(lambda o: o.strip().splitlines()[-1].split("\t")[2], out))
        check("http: test-status results are the run's tests", True,
              ev(lambda d: len(d["results"]) == OWN and all(set(r) == {"fullName", "status", "duration", "message", "stackTrace"}
                                                           for r in d["results"]), test_status_json("")))
    else:
        print('SKIP test run of Upipe\'s own tests: the manifest lacks "testables": ["com.upipe.server"]')
    NOMATCH = exec_json({"argv": ["test", "run", "--filter", "upipe-smoke-no-such-test"]})
    check("http: test run matching nothing fails", "Parameter Validation Failed True",
          ev(lambda d: "%s %s" % (d["error"], "No test ran" in d["errorDetails"]), NOMATCH))
    check("http: the failure carries the run", "completed 0",
          ev(lambda d: "%s %s" % (d["result"]["status"], d["result"]["summary"]["total"]), NOMATCH))
    check("http: synchronous playmode run refused", "Parameter Validation Failed",
          jget(exec_json({"argv": ["test", "run", "--mode", "playmode"]}), "error"))
    check("http: bad test mode", "Parameter Validation Failed", jget(exec_json({"argv": ["run_tests", "--mode", "player"]}), "error"))
    check("http: bad filter-type", "Parameter Validation Failed",
          jget(exec_json({"argv": ["test", "run", "--filter-type", "regex"]}), "error"))
    check("http: test status summary leaves out results", "Parameter Validation Failed False",
          ev(lambda d: "%s %s" % (d["error"], "results" in d["result"]),
             exec_json({"command": "test_status", "parameters": {"detail": "summary"}})))
    check("http: test status bad detail", "Parameter Validation Failed",
          jget(exec_json({"argv": ["test", "status", "--detail", "brief"]}), "error"))
    check("http: test-status route", "completed editor", ev(lambda d: "%s %s" % (d["status"], d["mode"]), test_status_json("?detail=summary")))
    check("http: test-status bad detail -> 400", 400, code("/api/test-status?detail=brief"))
    check("http: test-status needs the token", 401, code("/api/test-status", auth=False))
    check("cli: test cancel with no run going", "no_tests", ev(lambda d: result_of(d)["status"], cli_result("test", "cancel")))
    # PlayMode needs async-tests: entering Play Mode reloads the domain, and the run must still report afterwards.
    START = exec_json({"argv": ["run_tests", "--mode", "playmode", "--async-tests", "true", "--filter", "upipe-smoke-no-such-test"]})
    check("http: async playmode run answers running, without counts", "running playmode False",
          ev(lambda d: "%s %s %s" % (d["result"]["status"], d["result"]["mode"], "summary" in d["result"]), START))
    if not CANCEL_API:
        check("http: test cancel is not supported before test-framework 1.4", True,
              ev(lambda d: d["errorDetails"].startswith("NOT_SUPPORTED: canceling a test run needs com.unity.test-framework 1.4.0"),
                 exec_json({"argv": ["test", "cancel"]})))
    wait_test_run()
    check("play: the async playmode run reports across the reload", "completed playmode 0",
          ev(lambda d: "%s %s %s" % (d["status"], d["mode"], d["summary"]["total"]), test_status_json("?detail=summary")))
    check("play: back in Edit Mode after the run", 0, wait_field("playMode", "stopped"))
    until_ready()
    SMOKE_MODES = ev(lambda d: len({t["Mode"] for t in result_of(d) if "SmokePasses" in t["FullName"]}), LIST)
    if SMOKE_MODES == 2:
        ALL_START = exec_json({"argv": ["run_tests", "--mode", "all", "--async-tests", "true", "--filter", "SmokePasses"]})
        check("http: async mode=all starts", "running all",
              ev(lambda d: "%s %s" % (d.get("result", {}).get("status"), d.get("result", {}).get("mode")), ALL_START))
        wait_test_run()
        check("all: mode=all relays EditMode -> PlayMode", "completed all 2 2 0",
              ev(lambda d: "%s %s %s %s %s" % (d.get("status"), d.get("mode"), d.get("summary", {}).get("total"),
                                               d.get("summary", {}).get("passed"), d.get("summary", {}).get("failed")),
                 test_status_json("?detail=summary")))
        check("all: back in Edit Mode after mode=all", 0, wait_field("playMode", "stopped"))
        until_ready()
        if ev(lambda d: any(t["FullName"] == "UpipeFixtures.Play.SmokePlay.SmokePasses" for t in result_of(d)), LIST) is True:
            # A PlayMode-only name is checked once both halves ran, not missed after the EditMode half.
            exec_raw({"argv": ["run_tests", "--mode", "all", "--async-tests", "true", "--test-names",
                               "UpipeFixtures.SmokeEdit.SmokePasses;UpipeFixtures.Play.SmokePlay.SmokePasses"]})
            wait_test_run()
            check("all: test-names across both halves", "completed 2 []",
                  ev(lambda d: "%s %s %s" % (d.get("status"), d.get("summary", {}).get("passed"), d.get("missingTestNames")),
                     test_status_json("?detail=summary")))
            wait_field("playMode", "stopped")
            until_ready()
    else:
        print("SKIP mode=all SmokePasses: host project has no EditMode+PlayMode SmokePasses pair")
    # --test-names: exact names (a fixture or parameterized method runs all of it), and any name that ran nothing fails the run.
    if ev(lambda d: any(t["FullName"] == "UpipeFixtures.CalcTests.Add(1,2)" for t in result_of(d)), LIST) is True:
        def run_names(names):
            return cli_result("--timeout", "300", "test", "run", "--", "--test-names", names)

        check("cli: test run --test-names group, fixture and case", "completed 3 3",
              ev(lambda d: "%s %s %s" % (result_of(d)["status"], result_of(d)["summary"]["total"], result_of(d)["summary"]["passed"]),
                 run_names("UpipeFixtures.CalcTests.Add;UpipeFixtures.OtherFixture.Lone")))
        check("cli: test run --test-names keeps commas inside parentheses and quotes", 2,
              ev(lambda d: result_of(d)["summary"]["passed"],
                 run_names('UpipeFixtures.CalcTests.Quote("a,b"),UpipeFixtures.CalcTests.Add(1,2)')))
        MISSED = exec_json({"argv": ["test", "run", "--test-names", "UpipeFixtures.CalcTests.Add;UpipeFixtures.CalcTests.Ad"]})
        check("http: a test name that ran nothing fails the run", "Parameter Validation Failed True 2",
              ev(lambda d: "%s %s %s" % (d["error"], "TEST_NAMES_NOT_RUN: 1 of 2" in d["errorDetails"] and "UpipeFixtures.CalcTests.Ad" in d["errorDetails"],
                                         d["result"]["summary"]["passed"]), MISSED))
        check("http: test status keeps failing on the missed name", "Parameter Validation Failed ['UpipeFixtures.CalcTests.Ad']",
              ev(lambda d: "%s %s" % (d["error"], d["result"]["missingTestNames"]),
                 exec_json({"command": "test_status", "parameters": {"detail": "summary"}})))
        e_missed, _, missed_both = cli("command", "--project-path", PROJECT, "--timeout", "300",
                                      "test", "run", "--", "--test-names", "UpipeFixtures.CalcTests.Nope")
        check("cli: test run with a misspelled name exits non-zero", "yes yes",
              f"{'yes' if e_missed != 0 else ''} {'yes' if 'TEST_NAMES_NOT_RUN' in missed_both else ''}")
        check("http: test-names with filter refused", "Parameter Validation Failed",
              jget(exec_json({"argv": ["test", "run", "--filter", "Calc", "--test-names", "UpipeFixtures.CalcTests.Add"]}), "error"))
        check("http: structured test_names array", 2,
              ev(lambda d: d["result"]["summary"]["passed"],
                 exec_json({"command": "run_tests", "parameters": {"test_names": ["UpipeFixtures.CalcTests.Add"]}, "timeout": 300000})))
        # A detached synchronous run reports finished/total tests as the job's progress.
        J7 = detach("test", "run", "--test-names", "UpipeFixtures.SlowFixture")
        P7 = ""
        for _ in range(120):
            P7 = ev(lambda d: d["progress"]["message"] if d.get("progress") else "", job_json(J7))
            if re.search(r"^[1-3]/4", str(P7)):
                break
            time.sleep(0.25)
        check("job: detached test run reports progress", True,
              True if re.fullmatch(r"[1-3]/4 tests finished \(EditMode\)", str(P7)) else f"False ({P7}) {job_json(J7)}")
        _, j7_out = job_cli("wait", J7)
        check("cli: detached test run completes", "completed 4",
              ev(lambda d: "%s %s" % (d["data"]["state"], d["data"]["result"]["summary"]["passed"]), j7_out))
        if ev(lambda d: any(t["FullName"] == "UpipeFixtures.ReloadFixture.ReloadsTheDomain" for t in result_of(d)), LIST) is True:
            # A synchronous run whose test reloads the domain: the waiting request is answered as cut off, and the CLI does not resend it.
            e_reload, _, reload_both = cli("command", "--project-path", PROJECT, "--timeout", "300",
                                           "test", "run", "--", "--test-names", "UpipeFixtures.ReloadFixture.ReloadsTheDomain")
            check("cli: a synchronous run cut off by a domain reload is not retried", "yes yes",
                  f"{'yes' if e_reload != 0 else ''} {'yes' if 'TEST_RUN_INTERRUPTED' in reload_both else ''}")
            until_ready()
            # The framework finishes the resumed test after the reload; until then a new run is refused as in progress.
            after_reload = ""
            for _ in range(30):
                after_reload = ev(lambda d: "%s %s" % (result_of(d)["status"], result_of(d)["summary"]["passed"]),
                                  cli_result("--timeout", "300", "test", "run", "--", "--test-names", "UpipeFixtures.SmokeEdit.SmokePasses"))
                if after_reload == "completed 1":
                    break
                time.sleep(1)
            check("cli: a test run after the cut-off run passes", "completed 1", after_reload)
        else:
            print("SKIP synchronous run cut off by a reload: the host's HostFixtures copy lacks UpipeFixtures.ReloadFixture; copy Tests~/HostFixtures again")
        if CANCEL_API:
            # test cancel once the first test is in: the run ends canceled with only the tests that finished.
            exec_raw({"argv": ["run_tests", "--async-tests", "true", "--test-names", "UpipeFixtures.SlowFixture"]})
            wait_test_result()
            check("cli: test cancel stops an async EditMode run", "canceled editor True",
                  ev(lambda d: "%s %s %s" % (result_of(d)["status"], result_of(d)["mode"], 1 <= result_of(d)["summary"]["total"] < 4),
                     cli_result("test", "cancel")))
            check("http: test status fails a canceled run", "Command Execution Failed True",
                  ev(lambda d: "%s %s" % (d["error"], d["errorDetails"].startswith("TEST_RUN_CANCELED")),
                     exec_json({"argv": ["test", "status", "--detail", "summary"]})))
            # Left counting as running, the framework's runner logs an error at the next domain reload.
            check("eval: a canceled EditMode run leaves no test runner running", 0, eval_result(
                'var t = System.Type.GetType("UnityEditor.TestTools.TestRunner.EditModeRunner, UnityEditor.TestRunner"); '
                'var f = t == null ? null : t.GetField("RunningTests", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance); '
                'if (f == null) return "unresolved"; var n = 0; '
                'foreach (var o in UnityEngine.Resources.FindObjectsOfTypeAll(t)) if ((bool)f.GetValue(o)) n++; return n;'))
            # A detached synchronous run: the waiting request answers the canceled run, so its job fails.
            J8 = detach("test", "run", "--test-names", "UpipeFixtures.SlowFixture")
            wait_test_result()
            check("http: test_cancel stops a detached test run", "canceled",
                  ev(lambda d: d["result"]["status"], exec_json({"argv": ["test_cancel"]})))
            for _ in range(40):
                if job_state(J8) != "running":
                    break
                time.sleep(0.25)
            check("job: a canceled test run fails its job", "failed True",
                  ev(lambda d: "%s %s" % (d["state"], d["errorDetails"].startswith("TEST_RUN_CANCELED")), job_json(J8)))
            # mode all canceled in its EditMode half does not go on to the PlayMode half.
            exec_raw({"argv": ["run_tests", "--mode", "all", "--async-tests", "true", "--test-names",
                               "UpipeFixtures.SlowFixture;UpipeFixtures.Play.SlowPlay"]})
            wait_test_result()
            all_cancel = ev(lambda d: "%s %s" % (d["result"]["status"], d["result"]["mode"]), exec_json({"argv": ["test", "cancel"]}))
            check("all: mode=all canceled in its EditMode half skips Play Mode", "canceled all stopped",
                  f"{all_cancel} {status_field('playMode')}")
            # Past the point where the PlayMode half would have started: still canceled, no PlayMode result, never in Play Mode.
            time.sleep(3)
            all_after = ev(lambda d: "%s %s" % (d["status"], sum(r["fullName"].startswith("UpipeFixtures.Play.") for r in d.get("results", []))),
                           test_status_json("?detail=full"))
            check("all: a canceled mode=all never starts its PlayMode half", "canceled 0 stopped", f"{all_after} {status_field('playMode')}")
            if ev(lambda d: any(t["FullName"] == "UpipeFixtures.Play.SlowPlay.First" for t in result_of(d)), LIST) is True:
                # Canceled at once, on its way into Play Mode: at any moment the run ends canceled, out of Play Mode.
                exec_raw({"argv": ["run_tests", "--mode", "playmode", "--async-tests", "true", "--test-names", "UpipeFixtures.Play.SlowPlay"]})
                for _ in range(60):
                    if jget(exec_json({"argv": ["test", "cancel"]}), "success") is True:
                        break
                    time.sleep(0.25)
                wait_test_run()
                wait_field("playMode", "stopped")
                check("play: a PlayMode run canceled at once ends canceled out of Play Mode", "canceled stopped",
                      f"{jget(test_status_json('?detail=summary'), 'status')} {status_field('playMode')}")
                until_ready()
                # The run came across the reload into Play Mode with its tests going; cancel there stops Play Mode.
                exec_raw({"argv": ["run_tests", "--mode", "playmode", "--async-tests", "true", "--test-names", "UpipeFixtures.Play.SlowPlay"]})
                wait_test_result("PlayMode")
                check("play: test cancel stops a PlayMode run in Play Mode", "canceled playmode True",
                      ev(lambda d: "%s %s %s" % (result_of(d)["status"], result_of(d)["mode"], 1 <= result_of(d)["summary"]["total"] < 4),
                         cli_result("test", "cancel")))
                check("play: back in Edit Mode after the cancel", 0, wait_field("playMode", "stopped"))
                until_ready()
            else:
                print("SKIP PlayMode test cancel: the host's HostFixtures copy lacks UpipeFixtures.Play.SlowPlay; copy Tests~/HostFixtures again")
            check("cli: a test run right after a cancel passes", "completed 1",
                  ev(lambda d: "%s %s" % (result_of(d)["status"], result_of(d)["summary"]["passed"]),
                     cli_result("--timeout", "300", "test", "run", "--", "--test-names", "UpipeFixtures.SmokeEdit.SmokePasses")))
            if ev(lambda d: any(t["FullName"] == "UpipeFixtures.CleanupErrorFixture.PassesBeforeACleanupError" for t in result_of(d)), LIST) is True:
                # The run starts from a saved scene of its own; the open scenes are put back and the scene deleted afterwards.
                smoke_scene = "Assets/UpipeSmokeScene.unity"
                eval_result('using UnityEditor.SceneManagement; SessionState.SetString("UpipeSmoke.Scenes", string.Join("|", '
                            'EditorSceneManager.GetSceneManagerSetup().Select(s => s.path + "*" + s.isLoaded + "*" + s.isActive))); '
                            f'AssetDatabase.DeleteAsset("{smoke_scene}"); '
                            'return EditorSceneManager.SaveScene(EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single), '
                            f'"{smoke_scene}");')
                try:
                    # 1.4 turns an error logged by a cleanup into a run error; the failure says what passed, which requested names
                    # matched nothing (the error comes after the last RunFinished, so every result is in) and what was logged.
                    e_run_error, _, run_error_both = cli("command", "--project-path", PROJECT, "--timeout", "300", "test", "run", "--",
                                                         "--test-names", "UpipeFixtures.CleanupErrorFixture.PassesBeforeACleanupError;UpipeFixtures.NoSuchFixture.Typo")
                    check("cli: a run error reports the passes, the missing names and the errors logged", "yes yes yes yes",
                          f"{'yes' if e_run_error != 0 else ''} {'yes' if '1 of 1 tests passed' in run_error_both else ''} "
                          f"{'yes' if 'TEST_NAMES_NOT_RUN): UpipeFixtures.NoSuchFixture.Typo' in run_error_both else ''} "
                          f"{'yes' if 'cleanup error by design' in run_error_both else ''}")
                    # Its error mode skips the framework's scene restore, as a cancel does; Upipe reopens the scene on a later update.
                    active = None
                    for _ in range(20):
                        active = eval_result("return UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().path;")
                        if active == smoke_scene:
                            break
                        time.sleep(0.25)
                    check("cli: a run error reopens the scene the run started from", smoke_scene, active)
                finally:
                    eval_result('using UnityEditor.SceneManagement; var setup = SessionState.GetString("UpipeSmoke.Scenes", "")'
                                '.Split(new[] { "|" }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Split(new[] { "*" }, StringSplitOptions.None))'
                                '.Select(p => new SceneSetup { path = p[0], isLoaded = bool.Parse(p[1]), isActive = bool.Parse(p[2]) }).ToArray(); '
                                'SessionState.EraseString("UpipeSmoke.Scenes"); '
                                'if (setup.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(setup); '
                                'else EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single); '
                                f'return AssetDatabase.DeleteAsset("{smoke_scene}");')
            else:
                print("SKIP run error report: the host's HostFixtures copy lacks UpipeFixtures.CleanupErrorFixture; copy Tests~/HostFixtures again")
        else:
            print("SKIP test cancel of a running run: this project's com.unity.test-framework has no cancel API (needs 1.4.0)")
    else:
        print("SKIP test-names and test progress: the host lacks Tests~/HostFixtures")
else:
    print("SKIP test commands: the project has no com.unity.test-framework")
    check("no test framework: test list is unknown", "Command Not Found", jget(exec_json({"argv": ["test", "list"]}), "error"))

# --- capture game_view (a hidden camera of its own, so the open scene is neither changed nor dirtied)
check("http: capture bad source", "Parameter Validation Failed",
      jget(exec_json({"argv": ["capture", "game_view", "--source", "window"]}), "error"))
check("http: capture save path must be .png", "Parameter Validation Failed",
      jget(exec_json({"argv": ["capture_game_view", "--save_path", "a.jpg"]}), "error"))
check("http: capture negative max-resolution", "Parameter Validation Failed",
      jget(exec_json({"argv": ["capture", "game_view", "--max-resolution", "-1"]}), "error"))
check("http: capture width 0 refused", "Parameter Validation Failed",
      jget(exec_json({"argv": ["capture", "game_view", "--width", "0"]}), "error"))
check("http: capture_scene_view height 4097 refused", "Parameter Validation Failed",
      jget(exec_json({"argv": ["capture_scene_view", "--height", "4097"]}), "error"))
exec_raw({"argv": ["script", "eval", 'UnityEditor.EditorUtility.CreateGameObjectWithHideFlags("UpipeSmokeCamera", '
                   'UnityEngine.HideFlags.HideAndDontSave, typeof(UnityEngine.Camera)); return 1;']})
_, out, _ = cli("command", "--json", "--project-path", PROJECT, "capture", "game_view", "--",
                "--source", "camera", "--save-path", "Temp/upipe-smoke/camera.png")
if "NO_GPU" in out:
    print("SKIP capture: this Editor has no graphics device")
else:
    CAP = as_json(out)
    check("cli: capture to a file answers path and size only", "camera 1280 720 png False",
          ev(lambda d: "%s %s %s %s %s" % (result_of(d)["source"], result_of(d)["width"], result_of(d)["height"],
                                           result_of(d)["encoding"], "base64" in result_of(d)), CAP))

    def is_png(path):
        with open(path, "rb") as f:
            return f.read(8) == b"\x89PNG\r\n\x1a\n"

    check("cli: capture wrote a PNG", True, ev(is_png, os.path.join(IN_PROJECT, "camera.png")))
    check("cli: capture_game_view inline, scaled down", "64 36 True",
          ev(lambda d: "%s %s %s" % (result_of(d)["inlineWidth"], result_of(d)["inlineHeight"], len(result_of(d)["base64"]) > 0),
             cli_result("capture_game_view", "--", "--source", "camera", "--max_resolution", "64")))
    check("cli: camera capture at a custom size", "camera 320 200",
          ev(lambda d: "%s %s %s" % (result_of(d)["source"], result_of(d)["width"], result_of(d)["height"]),
             cli_result("capture", "game_view", "--", "--source", "camera", "--width", "320", "--height", "200")))
    # A batchmode Editor has no Scene view; an interactive one shows it, as a camera render or as the window drew it.
    if field("mode") == "batchmode":
        check("http: capture scene_view without a Scene view", True,
              ev(lambda d: d["errorDetails"].startswith("NO_SCENE_VIEW"), exec_json({"argv": ["capture", "scene_view"]})))
    else:
        check("cli: capture scene_view camera at a custom size", "camera 64 48 png True",
              ev(lambda d: "%s %s %s %s %s" % (result_of(d)["source"], result_of(d)["width"], result_of(d)["height"],
                                               result_of(d)["encoding"], len(result_of(d)["base64"]) > 0),
                 cli_result("capture", "scene_view", "--", "--width", "64", "--height", "48")))
        exec_raw({"argv": ["script", "eval",
                           "var sv = UnityEditor.SceneView.lastActiveSceneView; "
                           "if (sv != null) { sv.Show(); sv.Repaint(); } return sv != null;"]})
        time.sleep(1)
        check("cli: capture_scene_view screen source", "screen True",
              ev(lambda d: "%s %s" % (result_of(d)["source"], len(result_of(d)["base64"]) > 0),
                 cli_result("capture_scene_view", "--", "--source", "screen", "--max_resolution", "256")))
    asset_prefix = "assets" if os.path.exists(os.path.join(PROJECT, "assets")) else "Assets"
    check("cli: capture under Assets/ imports cleanly", True,
          ev(lambda d: d["success"], cli_result("capture", "game_view", "--", "--source", "camera", "--save-path",
                                                f"{asset_prefix}/UpipeSmokeTmp/cam.png")))
    check("cli: imported capture asset has a GUID", True,
          eval_result('return !string.IsNullOrEmpty(UnityEditor.AssetDatabase.AssetPathToGUID("Assets/UpipeSmokeTmp/cam.png"));'))
    exec_raw({"argv": ["script", "eval", 'return UnityEditor.AssetDatabase.DeleteAsset("Assets/UpipeSmokeTmp");']})
    shutil.rmtree(os.path.join(PROJECT, "Assets", "UpipeSmokeTmp"), ignore_errors=True)
    if os.path.exists(os.path.join(PROJECT, "Assets", "UpipeSmokeTmp.meta")):
        os.remove(os.path.join(PROJECT, "Assets", "UpipeSmokeTmp.meta"))
exec_raw({"argv": ["script", "eval", 'foreach (var c in UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.Camera>()) '
                   'if (c.name == "UpipeSmokeCamera") UnityEngine.Object.DestroyImmediate(c.gameObject); return 1;']})

# --- a domain reload: an unfinished job reads as failed afterwards, a finished one keeps its result
J4 = detach("script", "run", TOOL, "--entry", "Tool.Slow", "--args", "600000")
check("reload: long job running", 0, wait_job_state(J4, "running"))


def domain_mark():
    return eval_result('return System.AppDomain.CurrentDomain.GetData("upipe-smoke") ?? "new";')


def wait_new_domain():
    time.sleep(1)
    for _ in range(120):
        if domain_mark() == "new":
            return
        time.sleep(0.5)


# Raw HTTP so it is sent once. The method has run whatever the reply (a result, an error, or a connection dropped by
# the reload), so it must never be marked retryable: the CLI resends those, and each resend would reload again.
REPLY = exec_raw({"argv": ["script", "run", RELOAD_NOW]})
check("reload: a script run that reloads is not retryable", 0, REPLY.count('"retryable":true'))
# The old domain marks the job failed before its listener stops, so read the job only from the new domain.
wait_new_domain()
check("reload: the domain reloaded", "new", domain_mark())
check("reload: unfinished job reads as failed, not unknown", "failed Job Interrupted",
      ev(lambda d: "%s %s" % (d.get("state"), d.get("error")), job_json(J4)))
check("reload: finished job keeps its result", "completed 42", ev(lambda d: "%s %s" % (d.get("state"), d.get("result")), job_json(J1)))
rc, out = job_cli("wait", J4)
check("cli: job wait reports the interrupted job", "6 True",
      f"{rc} {ev(lambda d: 'failed: Job Interrupted' in d['errors'][0]['message'], out)}")
check("reload: Editor back", 0, until_ready())

# --- an eval that reloads, sent once through the CLI. The CLI resends retryable replies and each resend runs the
# script again, so count the runs in SessionState (it outlives the reload); the CLI's own reply depends on who wins
# the race with the reload, so it is not checked.
RUNS = f"upipe-smoke-eval-runs-{os.getpid()}"
cli("command", "--project-path", PROJECT, "eval",
    f'System.AppDomain.CurrentDomain.SetData("upipe-smoke", "old"); UnityEditor.SessionState.SetInt("{RUNS}", '
    f'UnityEditor.SessionState.GetInt("{RUNS}", 0) + 1); UnityEditor.EditorUtility.RequestScriptReload();')
wait_new_domain()
check("reload: an eval through the CLI reloaded the domain", "new", domain_mark())
check("reload: Editor back after the eval reload", 0, until_ready())
check("reload: the CLI ran the reloading eval once", 1, eval_result(f'return UnityEditor.SessionState.GetInt("{RUNS}", 0);'))

# --- the same, but the eval is still awaiting when the reload lands. It has started, so the reply must not be the
# retryable 503 (the CLI would resend it and run it again from the top). It reloads on its first run only, so a
# resend shows up as a second run rather than a reload loop.
CUT = f"upipe-smoke-cutoff-runs-{os.getpid()}"
_, _, cut_both = cli(
    "command", "--project-path", PROJECT, "--timeout", "90", "eval",
    f'System.AppDomain.CurrentDomain.SetData("upipe-smoke", "old"); '
    f'var runs = UnityEditor.SessionState.GetInt("{CUT}", 0) + 1; '
    f'UnityEditor.SessionState.SetInt("{CUT}", runs); '
    f'if (runs == 1) UnityEditor.EditorUtility.RequestScriptReload(); '
    f'await System.Threading.Tasks.Task.Delay(5000); return runs;',
)
check("reload: a reload cutting off an awaiting eval is reported, not resent", "yes",
      "yes" if "cut it off" in cut_both else "")
wait_new_domain()
check("reload: Editor back after the cut-off eval", 0, until_ready())
check("reload: the cut-off eval ran once", 1, eval_result(f'return UnityEditor.SessionState.GetInt("{CUT}", 0);'))
shutil.rmtree(SCRIPTS, ignore_errors=True)
shutil.rmtree(IN_PROJECT, ignore_errors=True)

sys.exit(FAILED)
