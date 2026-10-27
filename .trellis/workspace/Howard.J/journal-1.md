# Journal - Howard.J (Part 1)

> AI development session journal
> Started: 2026-10-27

---

## 2026-10-27 — tray app-identity icon: gates on this box

This WSL box has no libicu, and `Directory.Build.props` pins
`<InvariantGlobalization>false</InvariantGlobalization>`, so the SDK emits
`"System.Globalization.Invariant": false` into `bin/**/*.runtimeconfig.json`
— which **overrides** `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`. Builds work
with the env var, but `--selftest` / `dotnet test` crash on ICU unless you
first sed the emitted runtimeconfig in `bin/` to `"System.Globalization.Invariant": true`
and run `dotnet test --no-build`. Patch only the `bin/` copy, never the repo.
Pixel verification harness for tray work lives at `/tmp/iconprobe` (references
the repo's `PTMonitor.App.csproj`; do not commit it or its packages).

