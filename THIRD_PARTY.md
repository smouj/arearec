# Third-party software

## Native runtime

The native product uses only the .NET 8 SDK/runtime and Windows
platform APIs. The capture project targets
`net8.0-windows10.0.19041.0`, which supplies the supported C# Windows Runtime
projections without adding a third-party runtime or executable. Direct3D 11,
Windows Graphics Capture, Media Foundation and WASAPI remain Windows platform
APIs.

The self-contained x64 publish includes the .NET runtime files required by the
app; release packaging must preserve their license notices and list the exact
runtime version in the release record. No third-party executable or external
encoder is distributed.
