# CLAUDE.md

## Communication
Respond to the user in **French** during sessions. This file itself is in English for clarity/consistency.
Keep responses short. Do not add end-of-task summaries unless explicitly requested.

## Project
C# solution targeting **.NET 10**. All code, comments, console/UI output, commit messages and documentation are in **English**.

- `src/VcvPatchTools.Core`: reading and writing .vcv files (`Patch/`), module knowledge (`Catalog/`). No file I/O, so it runs in Blazor WASM.
  Don't use `System.Formats.Tar` in any library (PlatformNotSupported in the browser): `Patch/TarEntries.cs` reads and writes tar for that reason.
- `src/VcvPatchTools.Diagram`: analysis, layout, exporters. No file I/O.
- `src/VcvPatchTools.Bridge`: Rack <-> Cardinal conversion. No file I/O.
- `src/VcvPatchTools.Cli`: `vcvpatch` console app (info, convert, diagram, catalog build|cardinal), the only project touching files.
- `src/VcvPatchTools.Web`: standalone Blazor WebAssembly UI (Diagram, Info, Convert tabs), no server.
- `tests/VcvPatchTools.Tests`: one xUnit project, with folders per library.
- `catalog/`: `ports.json` (`make catalog`) and `cardinal.json` (`make cardinal-catalog`) are generated, `ports.overrides.json` and `roles.json` are hand-written.

## Development workflow
Use unit tests (xUnit, `tests/VcvPatchTools.Tests`) to drive development: write/update a test for new behavior or a bug before or alongside the fix, then run `dotnet test`. Don't consider a change done until the test suite passes.
Exporter output is pinned by golden files in `tests/golden`: after an intended change, `UPDATE_GOLDEN=1 dotnet test`, then review the diff.

Follow `.editorconfig`. Run `dotnet format` before considering a change done (fixes encoding/line-endings/indentation and naming). Note: `dotnet format` does **not** catch the `:silent`-severity rules (no `var` — always use explicit types; prefer expression-bodied members when the body is a single statement) — these must be respected manually when writing code.

Before committing and pushing a change, check whether the docs need updating with it: README text, the CLI option table, and the screenshots in `docs/screenshots` (add or redo one when the change is visible).
Add each user-visible change to the **Unreleased** section of `CHANGELOG.md`; a release renames that section to the version, and the release workflow publishes it as the release notes (it fails without one).

Never commit or push without an explicit request.
