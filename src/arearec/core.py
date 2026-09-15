from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import shutil


@dataclass(frozen=True)
class Region:
    x: int
    y: int
    width: int
    height: int

    @classmethod
    def from_points(cls, x1: int, y1: int, x2: int, y2: int) -> "Region":
        left, right = sorted((x1, x2))
        top, bottom = sorted((y1, y2))
        # H.264/yuv420p is happiest with even dimensions.
        width = max(2, right - left)
        height = max(2, bottom - top)
        width -= width % 2
        height -= height % 2
        return cls(left, top, width, height)

    @property
    def valid(self) -> bool:
        return self.width >= 2 and self.height >= 2


def resolve_ffmpeg(app_dir: Path) -> str | None:
    bundled = app_dir / "bin" / "ffmpeg.exe"
    if bundled.exists():
        return str(bundled)
    return shutil.which("ffmpeg")


def build_ffmpeg_command(
    ffmpeg: str,
    region: Region,
    fps: int,
    output: Path,
    draw_mouse: bool,
) -> list[str]:
    if fps not in (30, 60):
        raise ValueError("fps must be 30 or 60")
    if not region.valid:
        raise ValueError("invalid capture region")

    return [
        ffmpeg,
        "-hide_banner",
        "-loglevel", "warning",
        "-f", "gdigrab",
        "-framerate", str(fps),
        "-draw_mouse", "1" if draw_mouse else "0",
        "-offset_x", str(region.x),
        "-offset_y", str(region.y),
        "-video_size", f"{region.width}x{region.height}",
        "-i", "desktop",
        "-an",
        "-c:v", "libx264",
        "-preset", "ultrafast",
        "-crf", "20",
        "-pix_fmt", "yuv420p",
        "-movflags", "+faststart",
        "-y",
        str(output),
    ]
