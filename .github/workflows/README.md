# CI/CD Pipeline — UltimateClaudeMonitor

Event-driven pipeline (no cron). Workflows live here; their helper scripts live
in `scripts/`.

| File | Trigger | Purpose |
|------|---------|---------|
| `ci.yml` | push + PR on `main` + `workflow_call` | Build the whole solution on windows (no tests — this repo has no test project) |
| `release.yml` | **manual dispatch** | Build the app, then cut the dated `vyyyyMMdd` Release |
| `nightly.yml` | successful CI on `main` + manual | Publish `nightly-yyyyMMdd` prerelease and prune old ones |
| `_build.yml` | `workflow_call` (internal) | Publish the Windows app zip (no NuGet package is shipped) |
| `scripts/version.pl` | invoked by workflows | Stamp the project's own `<Version>` + its folder's commit count (`--stamp`) |
| `scripts/update-changelog.mjs` | invoked by workflows | Bucketise commits into release notes by `+ - * # !` prefix |
| `scripts/prune-nightlies.mjs` | invoked by workflows | GFS retention: 7 daily + 4 weekly + 3 monthly |

## Notes

- **No tests.** This repo ships a single net8.0-windows app (`ClaudeMonitor`)
  and no test project, so `ci.yml` is build-only.
- **No NuGet package.** Nothing is packed or pushed to nuget.org.
- **Versioning — files drive, never tags.** `ClaudeMonitor.csproj` carries its
  own `<Version>`; `version.pl --stamp` appends its folder commit count. There
  is no single repo version, so the repo-level Release/tag is the date marker
  `vyyyyMMdd`.
- Releases are cut by manual dispatch; nightlies and changelog notes are automatic.
