# v0.1 product contract

## User story
As a Windows user, I can drag a rectangle over my screen, start recording, stop recording and receive a playable MP4 containing that selected region.

## Acceptance criteria
1. Selection returns deterministic integer X/Y/width/height.
2. Recording works at 30 and 60 FPS.
3. Cursor can be included or excluded.
4. Stop finalizes a playable MP4 through the native Media Foundation sink.
5. No network request is made.
6. Missing native capture/encoding capability produces a clear actionable error instead of a crash.

Anything not required by these criteria is outside v0.1 unless it fixes a blocker.
