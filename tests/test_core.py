from pathlib import Path
from arearec.core import Region, build_ffmpeg_command


def test_region_normalizes_direction_and_even_dimensions():
    r = Region.from_points(200, 100, 101, 51)
    assert (r.x, r.y) == (101, 51)
    assert r.width % 2 == 0
    assert r.height % 2 == 0


def test_ffmpeg_command_contains_region_and_fps():
    r = Region(10, 20, 1280, 720)
    cmd = build_ffmpeg_command("ffmpeg", r, 60, Path("out.mp4"), False)
    joined = " ".join(cmd)
    assert "-framerate 60" in joined
    assert "-offset_x 10" in joined
    assert "-offset_y 20" in joined
    assert "-video_size 1280x720" in joined
    assert "-draw_mouse 0" in joined
