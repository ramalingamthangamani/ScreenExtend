"""Locating the bundled host executable and the .NET runtime it needs."""
from __future__ import annotations

import os
import platform
import shutil
import subprocess
import sys
import tempfile
import urllib.request
from pathlib import Path
from typing import Dict, List, Optional

DOTNET_MAJOR = 10
REQUIRED_RUNTIMES = ("Microsoft.WindowsDesktop.App", "Microsoft.AspNetCore.App")
DOTNET_INSTALL_SCRIPT = "https://dot.net/v1/dotnet-install.ps1"
DOTNET_DOWNLOAD_PAGE = "https://dotnet.microsoft.com/download/dotnet/10.0"

EXE_NAME = "ScreenExtend.exe"


def exe_path() -> Path:
    """Path of the bundled host executable (may not exist in a source checkout before build)."""
    return Path(__file__).resolve().parent / "bin" / EXE_NAME


def is_windows() -> bool:
    return sys.platform == "win32"


def user_dotnet_dir() -> Path:
    base = os.environ.get("LOCALAPPDATA") or str(Path.home() / "AppData" / "Local")
    return Path(base) / "Microsoft" / "dotnet"


def candidate_dotnet_roots() -> List[Path]:
    roots: List[Path] = []
    env_root = os.environ.get("DOTNET_ROOT")
    if env_root:
        roots.append(Path(env_root))
    for var in ("ProgramFiles", "ProgramW6432"):
        pf = os.environ.get(var)
        if pf:
            roots.append(Path(pf) / "dotnet")
    roots.append(user_dotnet_dir())
    on_path = shutil.which("dotnet")
    if on_path:
        roots.append(Path(on_path).resolve().parent)
    seen = set()
    unique = []
    for r in roots:
        key = str(r).lower()
        if key not in seen:
            seen.add(key)
            unique.append(r)
    return unique


def installed_runtimes(root: Path) -> Dict[str, List[str]]:
    """Runtime name -> versions found under a dotnet root (reads the shared/ folder, no process spawn)."""
    found: Dict[str, List[str]] = {}
    shared = root / "shared"
    if not shared.is_dir():
        return found
    for rt in shared.iterdir():
        if rt.is_dir():
            versions = [v.name for v in rt.iterdir() if v.is_dir()]
            if versions:
                found[rt.name] = sorted(versions)
    return found


def _major(version: str) -> int:
    try:
        return int(version.split(".")[0])
    except ValueError:
        return -1


def find_dotnet_root() -> Optional[Path]:
    """The first dotnet root that has every runtime the host needs at the right major version."""
    for root in candidate_dotnet_roots():
        rts = installed_runtimes(root)
        if all(any(_major(v) == DOTNET_MAJOR for v in rts.get(name, [])) for name in REQUIRED_RUNTIMES):
            return root
    return None


def missing_runtimes() -> List[str]:
    best: List[str] = list(REQUIRED_RUNTIMES)
    for root in candidate_dotnet_roots():
        rts = installed_runtimes(root)
        missing = [n for n in REQUIRED_RUNTIMES if not any(_major(v) == DOTNET_MAJOR for v in rts.get(n, []))]
        if len(missing) < len(best):
            best = missing
    return best


def install_runtime(log=print) -> Optional[Path]:
    """Installs the .NET Desktop and ASP.NET Core runtimes for the current user (no administrator rights)
    with Microsoft's dotnet-install script. Returns the dotnet root on success."""
    if not is_windows():
        return None
    target = user_dotnet_dir()
    target.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        script = Path(tmp) / "dotnet-install.ps1"
        log(f"Downloading {DOTNET_INSTALL_SCRIPT} ...")
        urllib.request.urlretrieve(DOTNET_INSTALL_SCRIPT, script)  # noqa: S310 - fixed https URL
        for runtime in ("windowsdesktop", "aspnetcore"):
            log(f"Installing .NET {DOTNET_MAJOR}.0 {runtime} runtime into {target} ...")
            cmd = [
                "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script),
                "-Channel", f"{DOTNET_MAJOR}.0", "-Runtime", runtime, "-InstallDir", str(target), "-NoPath",
            ]
            r = subprocess.run(cmd, check=False)
            if r.returncode != 0:
                log(f"dotnet-install failed for {runtime} (exit {r.returncode}).")
                return None
    rts = installed_runtimes(target)
    if all(any(_major(v) == DOTNET_MAJOR for v in rts.get(n, [])) for n in REQUIRED_RUNTIMES):
        return target
    return None


def environment_for(root: Path) -> Dict[str, str]:
    """Environment for launching the host so the apphost finds the runtime even when it lives in the user folder."""
    env = dict(os.environ)
    env["DOTNET_ROOT"] = str(root)
    env.setdefault("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
    return env


def describe() -> List[str]:
    lines = [
        f"Python {platform.python_version()} on {platform.platform()}",
        f"Host executable: {exe_path()} ({'present' if exe_path().exists() else 'MISSING'})",
    ]
    for root in candidate_dotnet_roots():
        rts = installed_runtimes(root)
        if rts:
            lines.append(f"dotnet root {root}:")
            for name, versions in sorted(rts.items()):
                lines.append(f"   {name}: {', '.join(versions)}")
    miss = missing_runtimes()
    lines.append("Required runtimes: " + ("all present" if not miss else "missing " + ", ".join(miss)))
    return lines
