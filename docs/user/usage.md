# Using AreaRec

## The workflow

`Select region → Record → Stop → MP4`

That is the whole product. AreaRec deliberately does not do anything after the
MP4 exists.

1. Start AreaRec. It opens a small window and stays in the tray.
2. Press **`Ctrl+Shift+R`** or use the record button to open the selection
   overlay.
3. Drag a rectangle over the part of the screen you want. Press **`Esc`** to
   cancel.
4. Recording starts on the region you selected. Press the hotkey again (or press
   Stop) to finish.
5. AreaRec finalizes the MP4, re-inspects it to confirm it is playable, and
   reports the saved path. The file lands in your configured output folder.

## What you control

| Setting | Options |
| --- | --- |
| Frame rate | 30 or 60 FPS |
| Cursor | Included or excluded from the recording |
| Quality | Balanced, High, Very High — mapped to a bits-per-pixel-per-frame target |
| Output folder | Anywhere; the last used folder is remembered |

Settings are stored as versioned JSON under `%LOCALAPPDATA%\AreaRec` and written
through a temporary sibling file with an atomic replace, so a crash mid-write
cannot corrupt them.

## How the recording is captured

- **Windows Graphics Capture** is the primary backend.
- If WGC cannot start, AreaRec retries with **DXGI Desktop Duplication**.
- Frames stay on the GPU through **Direct3D 11** crop, scale and readback.
- **Media Foundation** encodes H.264 into an MP4, using a hardware encoder MFT
  when one is available.

If capture or an encoder cannot start, AreaRec reports an actionable error
rather than crashing or presenting an empty file. Recordings are written to a
temporary file and moved into place only after finalization succeeds; a session
that captured zero frames is rejected instead of being saved as a broken video.

## What AreaRec does not do

The v0.1 contract is frozen around the shortest useful path, which means:

- no video editing, trimming or re-encoding;
- no webcam, annotations, drawing tools or overlays beyond the selection border;
- no account, cloud upload, sharing or telemetry;
- **system audio and microphone capture are not exposed in the UI** — the audio
  engine exists and has passed local smokes, but it stays off until it can be
  verified properly;
- multi-monitor composition code exists, but a recording spanning monitors with
  different DPI scales has **not been verified** on real hardware.

The contract and its acceptance criteria are in
[../development/product-scope.md](../development/product-scope.md).

## Known limitations

- Mixed-DPI and multi-monitor capture needs two-monitor hardware to verify; it
  is currently **not verified**.
- Recovery after a device is removed, a display is unplugged, or the machine
  suspends has **not been verified**.
- The 1080p / 1440p / 4K × 30/60 FPS performance matrix is incomplete; the
  numbers that do exist are in
  [../development/performance.md](../development/performance.md).
