"""Pure decisions of the BasHarness runner (no processes, no files): unit-tested in test_harness_logic.py."""

from __future__ import annotations

import os
import re
from dataclasses import dataclass, field
from typing import Dict, Iterable, List, Optional, Sequence, Tuple

APP_ID = "629730"
EXE = "BladeAndSorcery.exe"
DATA = "BladeAndSorcery_Data"
STREAMING = DATA + "/StreamingAssets"
# Copied, not linked: Wine/Unity derive the data folder from the executable's own path.
COPY_ROOT = {EXE, "UnityPlayer.dll", "UnityCrashHandler64.exe"}
# Folders the harness owns in its mirror: its own mods and the game's own log folder.
OWN_DIRS = {STREAMING + "/Mods", STREAMING + "/Logs"}

PREFIX = "[BasHarness]"
_STEP = re.compile(r"\[BasHarness\] STEP (\d+)/(\d+) (PASS|FAIL) line (\d+): (.*?)(?: -- (.*))?$")
_RESULT = re.compile(r"\[BasHarness\] RESULT (PASS|FAIL) (\d+)/(\d+)")
_ERROR = re.compile(r"\[BasHarness\] scenario error (.*)$")


def windows_path(posix: str) -> str:
    """What a Linux path is called inside Wine (drive Z: is the root)."""
    if not posix.startswith("/"):
        raise ValueError(f"absolute path expected: {posix}")
    return "Z:" + posix.replace("/", "\\")


def mirror_plan(root: Iterable[str], data: Iterable[str], streaming: Iterable[str]) -> Dict[str, str]:
    """Relative path -> 'copy' | 'link' | 'dir' for the isolated game mirror.

    Everything is a symlink into the real install except the executables (copied) and the
    folders on the way to the harness's own Mods and Logs (real folders)."""
    plan: Dict[str, str] = {}
    for name in root:
        if name == DATA:
            plan[name] = "dir"
        else:
            plan[name] = "copy" if name in COPY_ROOT else "link"
    plan[DATA] = "dir"
    for name in data:
        plan[f"{DATA}/{name}"] = "dir" if name == "StreamingAssets" else "link"
    plan[STREAMING] = "dir"
    for name in streaming:
        path = f"{STREAMING}/{name}"
        plan[path] = "dir" if path in OWN_DIRS else "link"
    for path in OWN_DIRS:
        plan[path] = "dir"
    return plan


def needs_copy(source: Tuple[int, float], target: Optional[Tuple[int, float]]) -> bool:
    """(size, mtime) of the game's file vs the mirror's copy: copy again after a game update."""
    return target is None or source != target


def game_args(level: str, mode: Optional[str]) -> List[str]:
    """Boot past the main menu into a level, with Unity's mock headset instead of SteamVR
    (GameManager.ProcessCommandLineArgs, PlayerControl.LoadXR in ThunderRoad 1.3.2)."""
    args = ["-vrmode", "mockhmd", "-skipintro", "-skipmodswarning", "-level", level]
    if mode:
        args += ["-levelmode", mode]
    return args


def launch_command(slr: str, proton: str, exe: str, args: Sequence[str], size: Tuple[int, int],
                   headless: bool) -> List[str]:
    """The game through the Steam Linux Runtime and Proton, past Steam's launch options;
    headless inside gamescope's own Xwayland (nothing on the desktop, no input grabbed)."""
    command = [slr, "--verb=waitforexitandrun", "--", proton, "waitforexitandrun", exe] + list(args)
    if not headless:
        return command
    width, height = size
    return (["gamescope", "--backend", "headless", "-W", str(width), "-H", str(height),
             "-w", str(width), "-h", str(height), "-r", "60", "--"] + command)


def launch_env(base: Dict[str, str], steam_dir: str, compat_dir: str, scenario_win: str,
               output_win: str) -> Dict[str, str]:
    env = dict(base)
    env.pop("PROTON_LOG", None)
    env.update({
        "STEAM_COMPAT_DATA_PATH": compat_dir,
        "STEAM_COMPAT_CLIENT_INSTALL_PATH": steam_dir,
        "SteamAppId": APP_ID,
        "SteamGameId": APP_ID,
        "BAS_HARNESS_SCENARIO": scenario_win,
        "BAS_HARNESS_OUTPUT": output_win,
    })
    return env


@dataclass
class StepReport:
    index: int
    total: int
    ok: bool
    line: int
    text: str
    detail: Optional[str]


@dataclass
class Report:
    steps: List[StepReport] = field(default_factory=list)
    result: Optional[Tuple[str, int, int]] = None
    errors: List[str] = field(default_factory=list)
    inert: bool = False


def parse_report(log: str) -> Report:
    """The harness mod's lines in Player.log (format: BasHarness.Core.Report)."""
    report = Report()
    for line in log.splitlines():
        if PREFIX not in line:
            continue
        step = _STEP.search(line)
        if step:
            report.steps.append(StepReport(int(step.group(1)), int(step.group(2)), step.group(3) == "PASS",
                                           int(step.group(4)), step.group(5), step.group(6)))
            continue
        result = _RESULT.search(line)
        if result:
            report.result = (result.group(1), int(result.group(2)), int(result.group(3)))
            continue
        error = _ERROR.search(line)
        if error:
            report.errors.append(error.group(1))
            continue
        if f"{PREFIX} inert:" in line:
            report.inert = True
    return report


def verdict(report: Report, timed_out: bool) -> Tuple[str, List[str]]:
    reasons: List[str] = []
    for error in report.errors:
        reasons.append(f"scenario error {error}")
    for step in report.steps:
        if not step.ok:
            reasons.append(f"step {step.index} (line {step.line}) failed: {step.text}"
                           + (f" -- {step.detail}" if step.detail else ""))
    if report.result is None:
        if report.inert:
            reasons.append("the harness mod ran inert: the scenario variable did not reach the game")
        if timed_out:
            reasons.append("timed out before the harness reported a result")
        else:
            reasons.append("the game ended without a harness result (crash, or the harness mod did not load)")
        return "FAIL", reasons
    return ("PASS" if report.result[0] == "PASS" and not reasons else "FAIL"), reasons


def mod_folder_problem(path: str, names: Optional[Sequence[str]]) -> Optional[str]:
    """Why a folder cannot be installed as a mod (names = its entries, None = not a folder)."""
    if names is None:
        return f"{path}: not a folder"
    if "manifest.json" not in names:
        return f"{path}: no manifest.json"
    return None


def install_name(path: str, names: Sequence[str]) -> str:
    """Folder name in the mirror's Mods: the mod's own DLL name for build outputs (bin/.../net472),
    else the folder's name. The game saves mod options keyed by this name."""
    base = os.path.basename(os.path.normpath(path))
    dlls = sorted(n[:-4] for n in names if n.lower().endswith(".dll"))
    if re.fullmatch(r"net\d+.*|Release|Debug", base) and dlls:
        return dlls[0]
    return base


def game_pids(processes: Iterable[Tuple[int, str, str]]) -> List[int]:
    """(pid, comm, cmdline) -> pids running the game's executable (not shells that mention it)."""
    pids = []
    for pid, comm, cmdline in processes:
        if comm in ("bash", "zsh", "sh", "fish", "grep", "pgrep", "python3", "python"):
            continue
        if EXE in cmdline:
            pids.append(pid)
    return pids


def steam_running(processes: Iterable[Tuple[int, str, str]]) -> bool:
    return any(comm == "steam" for _, comm, _ in processes)


def summary_markdown(name: str, result: str, report: Report, reasons: Sequence[str], run_dir: str,
                     seconds: float) -> str:
    lines = [f"# {name}: {result}", "", f"Run folder: `{run_dir}`  ", f"Duration: {seconds:.0f} s", ""]
    if reasons:
        lines += ["## Problems", ""] + [f"- {r}" for r in reasons] + [""]
    lines += ["## Steps", "", "| # | Result | Line | Step | Detail |", "|---|---|---|---|---|"]
    for s in report.steps:
        detail = (s.detail or "").replace("|", "\\|")
        lines.append(f"| {s.index} | {'PASS' if s.ok else 'FAIL'} | {s.line} | {s.text.replace('|', chr(92) + '|')} | {detail} |")
    if report.result:
        lines += ["", f"Harness result: {report.result[0]} {report.result[1]}/{report.result[2]}"]
    return "\n".join(lines) + "\n"


SLOW_FACTOR = 4

# GIFs stay small enough to share: 12 fps, at most 640 px wide, a palette per frame difference, ordered dithering
# (2026-11-20: 960 px at 15 fps made 17-185 MB per film). A slowed GIF only for films up to 6 s (4x = 24 s).
GIF_FILTER = ("fps=12,scale='min(640,iw)':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];"
              "[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle")
SLOW_GIF_MAX_SECONDS = 6


def film_commands(film: Dict, folder: str, ffmpeg: str = "ffmpeg") -> List[List[str]]:
    """ffmpeg calls that turn one film (its .film.json, written by the in-game Recorder) into videos.

    Per view a real-pace MP4 (the raw pictures come bottom row first, so they are flipped); with two views also both
    side by side; then the side-by-side (or the single view) slowed down 4x as MP4, a real-pace GIF and, for short
    films, a slow GIF.
    """
    name, w, h, fps, views = film["name"], int(film["width"]), int(film["height"]), int(film["fps"]), list(film["views"])
    if not views or int(film.get("frames", 0)) <= 0:
        return []
    p = lambda f: folder.rstrip("/") + "/" + f  # noqa: E731
    h264 = ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "20"]
    calls: List[List[str]] = []
    for view in views:
        calls.append([ffmpeg, "-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgba", "-s", f"{w}x{h}",
                      "-r", str(fps), "-i", p(f"{name}-{view}.rgba"), "-vf", "vflip", *h264, p(f"{name}-{view}.mp4")])
    if len(views) > 1:
        inputs: List[str] = []
        for view in views:
            inputs += ["-i", p(f"{name}-{view}.mp4")]
        calls.append([ffmpeg, "-y", "-loglevel", "error", *inputs, "-filter_complex", f"hstack=inputs={len(views)}",
                      *h264, p(f"{name}.mp4")])
        main = p(f"{name}.mp4")
    else:
        main = p(f"{name}-{views[0]}.mp4")
    calls.append([ffmpeg, "-y", "-loglevel", "error", "-i", main, "-vf", f"setpts={SLOW_FACTOR}*PTS", "-r", str(fps),
                  *h264, p(f"{name}-slow.mp4")])
    calls.append([ffmpeg, "-y", "-loglevel", "error", "-i", main, "-vf", GIF_FILTER, p(f"{name}.gif")])
    if int(film["frames"]) / fps <= SLOW_GIF_MAX_SECONDS:
        calls.append([ffmpeg, "-y", "-loglevel", "error", "-i", p(f"{name}-slow.mp4"), "-vf", GIF_FILTER, p(f"{name}-slow.gif")])
    return calls
