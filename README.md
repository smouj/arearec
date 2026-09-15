# AreaRec

**Select a region. Record it. Get an MP4.**

AreaRec is a tiny, local-first Windows screen recorder focused on one workflow: drag a rectangle over the screen and record exactly that area. No account, no cloud, no telemetry, no editor.

## Why AreaRec?

Most screen-recording tools are built as full production suites. AreaRec deliberately is not. Its v0.1 scope is frozen around the shortest useful path:

`Select region → Record → Stop → MP4`

## Features

- Mouse-driven region selection
- Exact X/Y/width/height capture
- 30 or 60 FPS
- Optional mouse cursor
- MP4/H.264 output
- Local-only processing through FFmpeg
- Zero network requirement at runtime
- No Python package dependencies beyond the standard library

## Requirements

- Windows 10/11
- Python 3.11+
- FFmpeg available on `PATH`, or `ffmpeg.exe` placed in `bin/`

## Run

```powershell
python -m pip install -e .
python -m arearec
```

Or after installation:

```powershell
arearec
```

## Design constraints

AreaRec v0.1 intentionally does **not** include system audio, microphone recording, video editing, webcam, annotations or advanced multi-monitor handling. See [ROADMAP](docs/ROADMAP.md).

## Privacy

AreaRec does not make network requests. Captures are written directly to the path chosen by the user.

## License

AreaRec source code is MIT licensed. FFmpeg is a separate project and is not included in this repository; redistribution must comply with the license of the FFmpeg build used. See [THIRD_PARTY.md](THIRD_PARTY.md).
