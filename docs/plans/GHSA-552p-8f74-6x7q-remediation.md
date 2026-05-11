# GHSA-552p-8f74-6x7q — Remediation Notes

**Branch:** `security/trust-gate-ghsa-552p-8f74-6x7q`
**Plan:** [2026-05-11-trust-gate-and-analyzer-allowlist.md](./2026-05-11-trust-gate-and-analyzer-allowlist.md)

## Comment to post on the GHSA advisory after merge

> Fix is merged and ships in `vX.Y.Z` (TODO: fill in after release). Summary of the mitigation:
>
> **Defense-in-depth, both layers must pass for analyzers to execute:**
>
> 1. **Per-solution trust gate.** `get_diagnostics` defaults `includeAnalyzers=false`. When the AI explicitly requests analyzer diagnostics, the server checks a `TrustStore` (`%APPDATA%\roslyn-codelens\trust.json` + in-memory session entries). Untrusted solutions return a structured error directing the AI to call the new `trust_solution` MCP tool. The Claude Code permission prompt on that tool call is the human checkpoint analogous to Visual Studio / Rider's "Trust this folder?" dialog. Solutions passed on the CLI at server startup are auto-trusted in session scope (matches user intent of "I just opened this server for this solution"). Trust scopes: `session`, `persistent`, `addRoot`.
>
>    The same gate also covers `get_code_fixes`, which the original advisory did not mention but which calls `AnalyzerRunner.RunAnalyzersAsync` unconditionally — the same code-execution primitive.
>
> 2. **Path-based analyzer allowlist.** Even for trusted solutions, only analyzer DLLs from known-safe locations are loaded. Default policy `nuget-and-solution-bin` accepts paths under `%USERPROFILE%\.nuget\packages\`, the dotnet SDK install root, and `<solution-dir>\**\bin\**`/`obj\**`. Solution-local DLLs outside `bin`/`obj` (e.g., `tools/EvilAnalyzer.dll` referenced via `<Analyzer Include="..\..\tools\EvilAnalyzer.dll" />`) are skipped before `GetAnalyzers()` is called. Stricter `strict` policy (NuGet+SDK only) and opt-out `all` policy are configurable via `trust.json`.
>
> **Why both layers?** The trust gate prevents the auto-load-on-clone attack chain (a single `git clone` + AI tool call is no longer enough). The allowlist prevents a deliberately malicious DLL from being loaded even from a solution the user trusted in good faith.
>
> **Known limitations documented in [SECURITY.md](../../SECURITY.md):** No hash pinning (poisoned NuGet cache still bypasses), no symlink resolution, source generators are not yet gated (they execute on `Compilation` creation, before any tool runs), analyzer execution is in-process rather than sandboxed, `NUGET_PACKAGES` env var and `nuget.config` `globalPackagesFolder` are not honored.
>
> **Test coverage:**
>
> - Unit: `TrustStoreTests` (11 tests including prefix-bypass regression + atomic-save + corrupt-file recovery), `AnalyzerAllowlistTests` (7 tests covering each policy + sibling-prefix bypass), `TrustStoreModelTests` (roundtrip + defaults).
> - Tool integration: `GetDiagnosticsToolTests` (untrusted-throws, untrusted+compiler-only-works, trusted+analyzers-works, default-flip via reflection), `TrustSolutionLogicTests`, `ListTrustedPathsToolTests`, `RevokeTrustToolTests`.
> - End-to-end: `AnalyzerRunnerTests.RunAnalyzersAsync_WithStrictAllowlist_FiltersAllAnalyzers` proves that even with real `AnalyzerReferences` present on the project, a strict policy filters them all before load.
>
> Thanks to @232-323 for the report.

## Items deferred (not closed by this PR — track separately if needed)

- Hash pinning of analyzer DLLs (SHA-256 allowlist)
- Source generator gating (different threat primitive; same DLL load mechanism)
- Subprocess sandbox for analyzer execution
- `NUGET_PACKAGES` env var and `nuget.config` `globalPackagesFolder` honor
- Symlink resolution in `AnalyzerAllowlist`

## CVSS

The advisory rates this 7.8 (High) on CVSS v3.1. The mitigation reduces this to substantially lower:

- `AV:L` → unchanged.
- `AC:L` → likely `AC:H` now (attacker needs a malicious analyzer DLL that lives under the victim's NuGet global packages folder, or convince the user to grant `trust_solution` while also placing a malicious DLL in `bin`/`obj`).
- `UI:R` → unchanged (user opens / asks AI to inspect the solution).
- Impact (C:H/I:H/A:H) → unchanged in worst case.

Reassessment for a future CVE record is out of scope for this remediation.
