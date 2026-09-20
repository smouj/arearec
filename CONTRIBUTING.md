# Contributing

Keep changes small and aligned with the product contract in `AGENTS.md`.

1. Create a focused branch.
2. Add or update tests for pure logic.
3. Run `dotnet restore AreaRec.sln --runtime win-x64`, then build the solution and run the native core/media smoke tests described in `docs/RELEASE.md`.
4. Open a pull request describing the user-visible change and Windows version tested.

Feature requests outside the current roadmap should explain why they belong in a minimal native region recorder. Do not add Python, FFmpeg,
external encoder processes, network services or telemetry to the native path.
