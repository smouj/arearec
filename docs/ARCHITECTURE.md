# Architecture

AreaRec is intentionally small.

```text
Tkinter UI
   ↓
RegionSelector → Region(x, y, width, height)
   ↓
FFmpeg command builder
   ↓
gdigrab → libx264 → MP4
```

## Modules

- `app.py`: window, selection overlay and FFmpeg process lifecycle.
- `core.py`: pure region normalization, FFmpeg lookup and command construction.

The pure core is tested without starting a GUI or FFmpeg process.
