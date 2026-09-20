# Contributing to AreaRec

AreaRec is intentionally small: `Select region → Record → Stop → MP4`. Changes
should make that path more reliable, measurable or maintainable without
turning the application into a general-purpose recording suite.

## Before opening a change

1. Restore and build `AreaRec.sln` in Release configuration.
2. Run `dotnet format AreaRec.sln --verify-no-changes --no-restore`.
3. Run the core tests and the relevant Windows smoke test from
   [docs/RELEASE.md](docs/RELEASE.md).
4. Run `git diff --check` and confirm no generated output is tracked.
5. Update [docs/VERIFICATION.md](docs/VERIFICATION.md) when evidence or
   hardware limits change.

The solution build is intentionally run without a runtime identifier. Apply
`--runtime win-x64` to restore/publish commands or to a project-level publish;
the .NET SDK does not support a RID on a solution-level build.

## Design rules

- Keep native resource ownership in the component that creates the resource.
- Keep capture, graphics, media, audio and recording lifecycle boundaries
  separate.
- Prefer deterministic tests for geometry, timing, settings and failure paths.
- Mark unavailable hardware, remote CI and visual acceptance as `NOT VERIFIED`.
- Do not add a dependency without documenting its license and necessity in
  `THIRD_PARTY.md`.
