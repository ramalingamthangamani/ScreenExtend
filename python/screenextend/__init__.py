"""ScreenExtend: use a phone or tablet as an extra monitor for a Windows PC.

The package bundles the ScreenExtend host (a small .NET executable) and a ``screenextend`` command that
checks for the .NET runtime, installs it per user when missing, and launches the host.
"""

__version__ = "1.0.0"
__all__ = ["__version__", "exe_path", "run"]

from .runtime import exe_path  # noqa: E402
from .cli import run  # noqa: E402
