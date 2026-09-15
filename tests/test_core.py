from pathlib import Path
from arearec.core import Region, build_ffmpeg_command


def test_region_normalizes_direction_and_even_dimensions():
    r = Region.from_points(200, 100, 101, 51)
    assert (r.x, r.y) == (101, 51)
    assert r.width % 2 == 0
    assert r.height % 2 == 0


def test_region_str_format():
    r = Region(10, 20, 1280, 720)
    s = str(r)
    assert "1280" in s
    assert "720" in s


def test_region_round_trip_dict():
    r = Region(10, 20, 1280, 720)
    d = r.to_dict()
    r2 = Region.from_dict(d)
    assert r2 == r


def test_ffmpeg_command_contains_region_and_fps():
    r = Region(10, 20, 1280, 720)
    cmd = build_ffmpeg_command("ffmpeg", r, 60, Path("out.mp4"), False)
    joined = " ".join(cmd)
    assert "-framerate 60" in joined
    assert "-offset_x 10" in joined
    assert "-offset_y 20" in joined
    assert "-video_size 1280x720" in joined
    assert "-draw_mouse 0" in joined


def test_ffmpeg_rejects_invalid_fps():
    r = Region(10, 20, 1280, 720)
    try:
        build_ffmpeg_command("ffmpeg", r, 24, Path("out.mp4"), True)
        assert False, "Expected ValueError"
    except ValueError:
        pass


def test_region_valid_minimum():
    r = Region(0, 0, 2, 2)
    assert r.valid is True


def test_region_invalid_too_small():
    r = Region(0, 0, 1, 1)
    assert r.valid is False