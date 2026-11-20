#!/usr/bin/env python3
"""Runs Blade & Sorcery headless with chosen mods and a scenario, and reports PASS/FAIL.

    python3 runner/bas_harness.py --mod <mod folder> [--mod ...] scenario.txt [scenario2.txt ...]

Nothing of the player's setup is touched:
- the game runs from a mirror (~/.local/share/bas-harness/game): symlinks into the real install,
  except its own Mods folder (only the mods under test + the harness mod) and its own Logs folder;
- Proton uses the harness's own prefix (~/.local/share/bas-harness/compatdata), so options and
  character saves are the harness's, never the player's;
- Unity's mock headset (-vrmode mockhmd) replaces SteamVR; gamescope's headless backend keeps the
  window off the desktop.
Scenarios given together run in order in the same prefix (e.g. set an option, restart, check it).
Exit code 0 = all PASS, 1 = a FAIL, 2 = could not run.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import signal
import subprocess
import sys
import time
from datetime import datetime
from pathlib import Path
from typing import List, Optional, Tuple

sys.path.insert(0, str(Path(__file__).resolve().parent))
import harness_logic as logic  # noqa: E402

HARNESS_ROOT = Path(__file__).resolve().parent.parent
STEAM = Path.home() / ".local/share/Steam"
DEFAULT_GAME = STEAM / "steamapps/common/Blade & Sorcery"
DEFAULT_HOME = Path.home() / ".local/share/bas-harness"
DEFAULT_SLR = STEAM / "steamapps/common/SteamLinuxRuntime_4/_v2-entry-point"
DEFAULT_PROTON = Path("/usr/share/steam/compatibilitytools.d/proton-cachyos-slr/proton")
HARNESS_MOD_PROJECT = HARNESS_ROOT / "src/BasHarness.Mod/BasHarness.Mod.csproj"
HARNESS_MOD_OUT = HARNESS_ROOT / "src/BasHarness.Mod/bin/Release/net472"
PLAYER_LOG = "pfx/drive_c/users/steamuser/AppData/LocalLow/Warpfrog/BladeAndSorcery/Player.log"


def say(text: str) -> None:
    print(f"[bas-harness] {text}", flush=True)


def processes() -> List[Tuple[int, str, str]]:
    result = []
    for entry in Path("/proc").iterdir():
        if not entry.name.isdigit():
            continue
        try:
            comm = (entry / "comm").read_text().strip()
            cmdline = (entry / "cmdline").read_bytes().replace(b"\0", b" ").decode("utf-8", "replace")
        except OSError:
            continue
        result.append((int(entry.name), comm, cmdline))
    return result


def preflight(args: argparse.Namespace) -> Optional[str]:
    procs = processes()
    running = logic.game_pids(procs)
    if running:
        return f"Blade & Sorcery is already running (pid {running}); close it first"
    if not logic.steam_running(procs):
        return "Steam is not running (the game needs it for its entitlement check)"
    for tool, path in (("Steam Linux Runtime", args.slr), ("Proton", args.proton)):
        if not Path(path).exists():
            return f"{tool} not found: {path}"
    if args.headless and shutil.which("gamescope") is None:
        return "gamescope not found (needed for --headless)"
    if not (Path(args.game) / logic.EXE).exists():
        return f"game not found: {args.game}"
    return None


def build_harness_mod() -> Optional[str]:
    dotnet = shutil.which("dotnet") or str(Path.home() / ".dotnet/dotnet")
    say("building the harness mod")
    done = subprocess.run([dotnet, "build", str(HARNESS_MOD_PROJECT), "-c", "Release", "-nologo", "-v", "q"],
                          capture_output=True, text=True)
    if done.returncode != 0:
        return "harness mod build failed:\n" + done.stdout[-2000:] + done.stderr[-2000:]
    return None


def prepare_mirror(game: Path, mirror: Path) -> None:
    """Idempotent: links into the real install, copies of the executables, own Mods and Logs."""
    data, streaming = game / logic.DATA, game / logic.STREAMING
    plan = logic.mirror_plan([p.name for p in game.iterdir()],
                             [p.name for p in data.iterdir()],
                             [p.name for p in streaming.iterdir()])
    for rel, action in sorted(plan.items()):
        source, target = game / rel, mirror / rel
        if action == "dir":
            if target.is_symlink():
                target.unlink()
            target.mkdir(parents=True, exist_ok=True)
        elif action == "link":
            if target.is_symlink() and os.readlink(target) == str(source):
                continue
            if target.is_symlink() or target.is_file():
                target.unlink()
            target.symlink_to(source)
        else:
            s = source.stat()
            t = target.stat() if target.exists() and not target.is_symlink() else None
            if logic.needs_copy((s.st_size, s.st_mtime), (t.st_size, t.st_mtime) if t else None):
                if target.is_symlink():
                    target.unlink()
                shutil.copy2(source, target)


def install_mods(mirror: Path, mod_dirs: List[Path]) -> List[str]:
    mods = mirror / logic.STREAMING / "Mods"
    for child in mods.iterdir():
        shutil.rmtree(child) if child.is_dir() and not child.is_symlink() else child.unlink()
    installed = []
    for source in [HARNESS_MOD_OUT] + mod_dirs:
        name = logic.install_name(str(source), os.listdir(source))
        shutil.copytree(source, mods / name, ignore=shutil.ignore_patterns("*.xml"))
        installed.append(name)
    return installed


def kill_session(proc: subprocess.Popen) -> None:
    try:
        os.killpg(proc.pid, signal.SIGTERM)
    except ProcessLookupError:
        return
    try:
        proc.wait(timeout=15)
    except subprocess.TimeoutExpired:
        try:
            os.killpg(proc.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass


def kill_mirror_game(mirror: Path) -> None:
    """Wine processes can outlive the session; end any left running from the mirror."""
    for pid, _, cmdline in processes():
        if logic.EXE in cmdline and ("bas-harness" in cmdline or str(mirror) in cmdline):
            try:
                os.kill(pid, signal.SIGKILL)
            except ProcessLookupError:
                pass


def run_scenario(args: argparse.Namespace, scenario: Path, mirror: Path, home: Path, run_root: Path) -> str:
    name = scenario.stem
    run_dir = run_root / name
    run_dir.mkdir(parents=True, exist_ok=True)
    shutil.copy2(scenario, run_dir / scenario.name)
    compat = home / "compatdata"
    compat.mkdir(parents=True, exist_ok=True)
    log = compat / PLAYER_LOG
    if log.exists():
        log.unlink()

    env = logic.launch_env(dict(os.environ), str(STEAM), str(compat),
                           logic.windows_path(str(run_dir / scenario.name)), logic.windows_path(str(run_dir)))
    command = logic.launch_command(str(args.slr), str(args.proton), str(mirror / logic.EXE),
                                   logic.game_args(args.level, args.level_mode), args.size, args.headless)
    (run_dir / "launch.txt").write_text(" ".join(command) + "\n" + "\n".join(
        f"{k}={env[k]}" for k in sorted(env) if k.startswith(("STEAM_COMPAT", "Steam", "BAS_HARNESS"))) + "\n")
    say(f"{name}: starting the game {'headless' if args.headless else 'in a window'} ({args.level})")
    started = time.time()
    with open(run_dir / "proton.log", "wb") as out:
        proc = subprocess.Popen(command, env=env, cwd=str(mirror), stdout=out, stderr=subprocess.STDOUT,
                                start_new_session=True)
        timed_out = False
        result_at: Optional[float] = None
        while True:
            text = log.read_text(errors="replace") if log.exists() else ""
            if result_at is None and "[BasHarness] RESULT" in text:
                result_at = time.time()
            if proc.poll() is not None:
                break
            if result_at is not None and time.time() - result_at > 30:
                break  # reported; the game's own quit hangs -> end it
            if time.time() - started > args.timeout:
                timed_out = True
                break
            time.sleep(1)
        kill_session(proc)
        kill_mirror_game(mirror)

    text = log.read_text(errors="replace") if log.exists() else ""
    (run_dir / "Player.log").write_text(text)
    make_films(run_dir)
    report = logic.parse_report(text)
    result, reasons = logic.verdict(report, timed_out)
    seconds = time.time() - started
    (run_dir / "summary.md").write_text(logic.summary_markdown(name, result, report, reasons, str(run_dir), seconds))
    for step in report.steps:
        say(f"  {'PASS' if step.ok else 'FAIL'} {step.text}" + (f" -- {step.detail}" if step.detail else ""))
    for reason in reasons:
        say(f"  ! {reason}")
    say(f"{name}: {result} ({seconds:.0f} s) -> {run_dir / 'summary.md'}")
    return result


def make_films(run_dir: Path) -> None:
    """Turns every film the scenario recorded (record-start ... record-stop) into MP4 and GIF; drops the raw pictures."""
    for sidecar in sorted(run_dir.glob("*.film.json")):
        film = json.loads(sidecar.read_text())
        calls = logic.film_commands(film, str(run_dir))
        failed = next((c for c in calls if subprocess.run(c).returncode != 0), None)
        if failed is not None:
            say(f"film {film['name']}: ffmpeg failed on {failed[-1]}; raw pictures kept")
            continue
        for raw in run_dir.glob(f"{film['name']}-*.rgba"):
            raw.unlink()
        say(f"film {film['name']}: {film['frames']} frames at {film['fps']} fps ({film['repeated']} repeated) -> "
            f"{run_dir / (film['name'] + ('.mp4' if len(film['views']) > 1 else '-' + film['views'][0] + '.mp4'))}")


def parse_args(argv: List[str]) -> argparse.Namespace:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("scenarios", nargs="+", type=Path)
    p.add_argument("--mod", action="append", default=[], type=Path, help="mod folder with manifest.json (repeatable)")
    p.add_argument("--level", default="Arena")
    p.add_argument("--level-mode", default="Sandbox")
    p.add_argument("--timeout", type=float, default=300, help="seconds per scenario")
    p.add_argument("--artifacts", type=Path, default=Path.cwd() / "artifacts/harness")
    p.add_argument("--game", type=Path, default=DEFAULT_GAME)
    p.add_argument("--home", type=Path, default=DEFAULT_HOME, help="mirror + prefix location")
    p.add_argument("--slr", type=Path, default=DEFAULT_SLR)
    p.add_argument("--proton", type=Path, default=DEFAULT_PROTON)
    p.add_argument("--size", default="1280x720")
    p.add_argument("--window", dest="headless", action="store_false", help="visible window instead of headless")
    p.add_argument("--fresh-prefix", action="store_true", help="delete the harness prefix first (new character)")
    args = p.parse_args(argv)
    width, _, height = args.size.partition("x")
    args.size = (int(width), int(height))
    return args


def main(argv: List[str]) -> int:
    args = parse_args(argv)
    problem = preflight(args)
    for mod in args.mod:
        problem = problem or logic.mod_folder_problem(str(mod), os.listdir(mod) if mod.is_dir() else None)
    for scenario in args.scenarios:
        if not scenario.is_file():
            problem = problem or f"scenario not found: {scenario}"
    if problem:
        say(f"cannot run: {problem}")
        return 2

    home = args.home
    home.mkdir(parents=True, exist_ok=True)
    lock = home / "run.lock"
    try:
        fd = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
    except FileExistsError:
        say(f"cannot run: another harness run holds {lock} (delete it if no run is active)")
        return 2
    os.write(fd, str(os.getpid()).encode())
    os.close(fd)
    try:
        problem = build_harness_mod()
        if problem:
            say(f"cannot run: {problem}")
            return 2
        if args.fresh_prefix and (home / "compatdata").exists():
            shutil.rmtree(home / "compatdata")
        mirror = home / "game"
        prepare_mirror(args.game, mirror)
        installed = install_mods(mirror, [m.resolve() for m in args.mod])
        say("mods in the mirror: " + ", ".join(installed))
        run_root = args.artifacts / datetime.now().strftime("%Y%m%d-%H%M%S")
        results = [run_scenario(args, s.resolve(), mirror, home, run_root) for s in args.scenarios]
        overall = "PASS" if all(r == "PASS" for r in results) else "FAIL"
        say(f"overall: {overall} ({results.count('PASS')}/{len(results)} scenarios) -> {run_root}")
        return 0 if overall == "PASS" else 1
    finally:
        lock.unlink(missing_ok=True)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
