"""The ``screenextend`` command: launches the bundled host, installs the .NET runtime if needed."""
from __future__ import annotations

import argparse
import subprocess
import sys
import time
from typing import List, Optional, Sequence

from . import runtime

# Host options that produce console output and should run attached to this terminal.
ATTACHED_FLAGS = {"--no-tray", "--headless", "--install-driver", "--driver-test", "--help", "-h", "/?"}

HOST_USAGE = """\
Host options (passed through to ScreenExtend.exe):
  --port N, -p N      port to listen on (default 8700; 8701-8709 tried when busy)
  --no-tray           run attached to this console instead of the notification area; Ctrl+C to quit
  --no-browser        do not open the connection page at startup
  --install-driver    download and install the virtual display driver (UAC prompt), then exit
  --driver-test       diagnostic: plug one virtual display, report what Windows sees, unplug it
  --data-dir PATH     data folder (default %APPDATA%\\ScreenExtend)
  --webroot PATH      serve web files from a folder (UI development)
  --verbose, -v       verbose logging
"""


def _parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="screenextend",
        description="Use a phone or tablet as an extra monitor for this Windows PC. "
        "Starts the ScreenExtend host; open the connection page it shows and scan the QR code on the phone.",
        epilog=HOST_USAGE,
        formatter_class=argparse.RawDescriptionHelpFormatter,
        add_help=False,
    )
    p.add_argument("--help", "-h", action="store_true", help="show this help")
    p.add_argument("--version", action="store_true", help="print the package version")
    p.add_argument("--doctor", action="store_true", help="check the host executable and .NET runtime, then exit")
    p.add_argument("--install-runtime", action="store_true", help="install the .NET runtime for this user, then exit")
    p.add_argument("--no-install-runtime", action="store_true", help="never install the runtime automatically")
    p.add_argument("--path", action="store_true", help="print the path of the bundled ScreenExtend.exe")
    return p


def _ensure_runtime(auto_install: bool, log=print):
    root = runtime.find_dotnet_root()
    if root is not None:
        return root
    missing = ", ".join(runtime.missing_runtimes())
    log(f"The .NET {runtime.DOTNET_MAJOR} runtime is not installed ({missing}).")
    if not auto_install:
        log(f"Install it from {runtime.DOTNET_DOWNLOAD_PAGE} (Desktop Runtime and ASP.NET Core Runtime, x64) "
            "or run: screenextend --install-runtime")
        return None
    log("Installing it for the current user (no administrator rights needed; a few hundred MB)...")
    root = runtime.install_runtime(log)
    if root is None:
        log(f"Automatic install failed. Install .NET {runtime.DOTNET_MAJOR} manually from {runtime.DOTNET_DOWNLOAD_PAGE} "
            "(Desktop Runtime and ASP.NET Core Runtime, x64).")
    return root


def run(host_args: Sequence[str] = (), auto_install_runtime: bool = True, wait: Optional[bool] = None) -> int:
    """Launches the host with the given arguments. Returns the exit code (0 when started detached)."""
    if not runtime.is_windows():
        print("ScreenExtend runs on Windows 10/11 only (it creates virtual displays with a Windows driver).", file=sys.stderr)
        return 2
    exe = runtime.exe_path()
    if not exe.exists():
        print(f"The host executable is missing: {exe}\nReinstall the package (pip install --force-reinstall screenextend).", file=sys.stderr)
        return 3
    root = _ensure_runtime(auto_install_runtime)
    if root is None:
        return 4
    env = runtime.environment_for(root)
    attached = wait if wait is not None else any(a.lower() in ATTACHED_FLAGS for a in host_args)
    cmd = [str(exe), *host_args]
    if attached:
        try:
            return subprocess.call(cmd, env=env)
        except KeyboardInterrupt:
            return 0
    # Tray mode: start detached so closing this console does not stop the host.
    flags = getattr(subprocess, "DETACHED_PROCESS", 0) | getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)
    proc = subprocess.Popen(cmd, env=env, creationflags=flags, close_fds=True,  # noqa: S603
                            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(1.5)
    if proc.poll() is not None and proc.returncode not in (0, None):
        print(f"ScreenExtend exited immediately with code {proc.returncode}. Run 'screenextend --no-tray' to see why.", file=sys.stderr)
        return proc.returncode
    print("ScreenExtend is running in the notification area (tray icon).")
    print("The connection page opens in your browser; on the phone, scan its QR code or open the address it shows.")
    print("Tip: 'screenextend --no-tray' runs it in this console; the tray menu has Quit.")
    return 0


def main(argv: Optional[List[str]] = None) -> int:
    argv = list(sys.argv[1:] if argv is None else argv)
    parser = _parser()
    ns, host_args = parser.parse_known_args(argv)
    if ns.help:
        parser.print_help()
        return 0
    if ns.version:
        from . import __version__
        print(__version__)
        return 0
    if ns.path:
        print(runtime.exe_path())
        return 0
    if ns.doctor:
        for line in runtime.describe():
            print(line)
        return 0 if runtime.exe_path().exists() and not runtime.missing_runtimes() else 1
    if ns.install_runtime:
        if not runtime.is_windows():
            print("Windows only.", file=sys.stderr)
            return 2
        root = runtime.install_runtime()
        print("Installed." if root else "Install failed.")
        return 0 if root else 1
    return run(host_args, auto_install_runtime=not ns.no_install_runtime)


if __name__ == "__main__":
    raise SystemExit(main())
