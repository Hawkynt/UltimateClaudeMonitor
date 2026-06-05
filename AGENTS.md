# Agent guide — UltimateClaudeMonitor

Working agreement for **all** coding agents and human contributors working in
this repository. These rules are not optional. The full house spec lives in
the `Hawkynt/project-template` repo (`STANDARD.md`); this file is the
per-repo distillation.

## What this is

A **single-file C# console app** (`ClaudeMonitor.cs` + `ClaudeMonitor.sln`,
PowerShell launcher `ClaudeMonitor.ps1`) that live-monitors all running
Claude Code processes: token usage, costs, burn rates, account-aware
forecasting. Windows-targeted; reads `~/.claude/`.

## Commits

- **Group changes semantically/logically** — one concern per commit.
- **Every subject line starts with a prefix**: `+` added · `-` removed ·
  `*` changed · `#` bug fixed · `!` critical todo.
- Never start a subject with "fix"/"bugfix"/"changed"/"modified".
- **No AI traces anywhere**: no `Co-Authored-By` AI lines, no "Generated
  with" footers, no agent mentions in messages, comments, or authorship.

## The loop (always, in this order)

1. **Before committing**: `dotnet build ClaudeMonitor.sln -c Release` until
   green, and run the monitor against a live Claude Code session for
   behavior changes. Update the README's feature/known-bugs sections when
   behavior changes.
2. **Commit** (rules above) and **push**.
3. **Wait for CI**; on `main` a green CI triggers the nightly (prerelease +
   GFS prune, same-day replace). Fix and loop until everything is green.

Stable releases are **manual** (`gh workflow run release.yml`) — never cut
one unless explicitly asked.

## Code conventions

- Latest C# features; the app deliberately stays **one file** — keep it that
  way, organized by partial-class-style regions rather than new files.
- Parsing of Claude's local files is defensive: malformed/missing data must
  degrade to "unknown", never crash the monitor loop.
- Cost/limit math changes get called out explicitly in the commit body —
  wrong numbers are worse than no numbers.

## README & repo conventions

- Standard frame: title → badges → one-line `>` blockquote; fixed emoji
  mapping for the standard sections (`## 🛠️ Build/Test/Run Guidelines`,
  `## ❤️ Support`, `## 📜 License`); repo-specific sections keep their
  consistent topical emojis.
- License is LGPL-3.0-or-later; the `## ❤️ Support` section and
  `.github/FUNDING.yml` stay intact.
