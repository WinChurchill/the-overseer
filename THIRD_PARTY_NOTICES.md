# Third-party notices

No third-party source code or upstream presentation assets have been incorporated
in this bootstrap. The supplied website PNGs are user-provided design references.

## Build and test dependencies

- Microsoft .NET / WPF: platform SDK and runtime, governed by their distributed licenses.
- Microsoft.NET.Test.Sdk: test infrastructure, MIT license.
- xUnit.net (`xunit`): test framework, Apache-2.0 license.
- xUnit.net Visual Studio runner (`xunit.runner.visualstudio`): test adapter, Apache-2.0 license.

Exact package versions are pinned in `tests/TheOverseer.Tests/TheOverseer.Tests.csproj`.
Dependency license metadata remains available in the restored NuGet packages.

## Planned monitoring reuse

The frozen specification identifies https://github.com/LH-03/codex-monitor-hud
as a candidate source of monitoring plumbing. No code has been copied from it.
When code is adapted, record its exact source paths and revision, inspect the
applicable license, and preserve required copyright/license notices here and
with the adapted code. Do not import its presentation layer.
