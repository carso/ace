# ACE Plugin User Guide

**ACE — Agent Context Engine** · Qoder Plugin · Version 0.1.0

---

## 1. Overview

ACE (Agent Context Engine) is a **local-first developer intelligence platform** that gives AI coding agents deep, structured understanding of your repository. The ACE Qoder plugin (`ace`, displayName "ACE - Agent Context Engine") provides:

- **Change impact analysis** — what a change affects, directly and indirectly
- **Risk assessment** — deterministic risk level and a 0–100 risk score with weighted factors
- **Affected tests** — which tests to run, each with a selection reason
- **Regression scope** — recommended testing scope (affected unit tests vs. full regression)
- **Repository context** — prioritized, query-aware context across 7 tiers (code, dependencies, impact, tests, config, architecture, repository)

### Design principles

- **Engine First.** All intelligence lives in `Ace.Core`. The MCP server and the CLI (`ace.exe`) are thin adapters over the same engine — you get **identical answers** everywhere.
- **Local-first.** Index data is stored under `<repo>/.ace` inside your repository. **No source code leaves your machine.**
- **Lazy & incremental.** ACE indexes a repository on first use and updates the index incrementally afterward — no manual warm-up required.

> **Platform note:** the current package is **Windows x64 only**.

---

## 2. Requirements & Installation

### 2.1 Build the plugin package

From the repository root, run:

```powershell
pwsh ./build.ps1
```

Optionally skip the test suite for a faster build:

```powershell
pwsh ./build.ps1 -SkipTests
```

The build script:

1. Builds the solution in **Release** configuration
2. Runs the tests (unless `-SkipTests`)
3. Publishes self-contained single-file executables:
   - `dist\win-x64\Ace.Mcp.Server.exe` (MCP server)
   - `dist\win-x64\ace.exe` (CLI)
4. Stages the MCP server into `plugin\qoder\dist\win-x64\`
5. Creates **`plugin\ace-qoder.zip`** — the zip contains the *contents* of `plugin\qoder`, with `.qoder-plugin/plugin.json` at the archive root

### 2.2 Install in Qoder

Install `plugin\ace-qoder.zip` through **Qoder's local plugin install UI**.

Once installed, the plugin's `.mcp.json` launches the bundled MCP server (`dist/win-x64/Ace.Mcp.Server.exe`) using **stdio transport** — no separate server setup is needed.

### 2.3 Reinstall caveat

Uninstalling the plugin may **leave folders behind** under:

- `~/.qoder/plugins/custom`
- `~/.qoder/plugins/cache/local`

If you reinstall or update the plugin, manually delete these leftover folders first to avoid stale files interfering with the new installation.

---

## 3. Configuration (`ace.json`)

ACE reads per-repository configuration from an `ace.json` file at the **repository root**. All values live under an `"ace"` section:

```json
{
  "ace": {
    "indexPath": ".ace",
    "maxParallelism": 4,
    "enableGitAnalysis": true,
    "enableArchitectureAnalysis": true
  }
}
```

### Precedence

Configuration values resolve in this order (later wins):

1. **Built-in defaults**
2. **`ace.json`** at the repository root
3. **`ACE__*` environment variables** (e.g. `ACE__enableGitAnalysis`)

### Key options

| Option | Default | Description |
|---|---|---|
| `indexPath` | `.ace` | Where index data is stored, relative to the repo root |
| `enableGitAnalysis` | `false` | Gates git working-tree and diff-range inputs |
| `enableArchitectureAnalysis` | `true` | Enables architecture layering checks |
| `exclusionPatterns` | — | File/directory patterns excluded from indexing |
| `sensitiveFilePatterns` | — | Patterns identifying sensitive files |
| `architectureRules` | — | Custom layering/architecture rules |

### ⚠️ Important gotcha: `enableGitAnalysis`

The `/impact` and `/regression` commands default to analyzing the **git working tree** when you pass no files or diff range — but this **only works when `enableGitAnalysis: true`** (it defaults to `false`).

If you don't enable it, either:

- set `"enableGitAnalysis": true` in `ace.json`, **or**
- pass **explicit file lists** (or a `--diff` range requires it too — see below) to the commands.

---

## 4. Quick Start

A typical workflow:

1. **Open your repository** in Qoder.
2. **Ask the agent for repository context** — e.g. *"Give me an overview of this repository"* or *"How does CustomerService fit into the architecture?"* The `repository-context` skill triggers automatically; ACE indexes the repo lazily on first use.
3. **Make your changes** as usual.
4. **Run `/impact`** before reviewing or committing — e.g. `/impact src/Customer.Services/CustomerService.cs` — to see risk, affected components, projects, APIs and tests.
5. **Run `/regression`** to get a recommended testing scope for the change, e.g. `/regression --files src/Customer.Services/CustomerService.cs`.

That's it — no manual indexing or warm-up required.

---

## 5. Slash Commands

### 5.1 `/impact` — Analyze change impact, risk and affected components/tests

**Arguments:**

| Argument | Required | Description |
|---|---|---|
| `<files...>` | Optional | Repo-relative paths of changed files (positional) |
| `--diff <range>` | Optional | Git diff range, e.g. `HEAD~1..HEAD` |

If neither is provided, the command uses the **git working tree** — which requires `enableGitAnalysis: true` (see §3).

**Examples:**

```text
/impact src/Customer.Services/CustomerService.cs

/impact src/Customer.Domain/Customer.cs src/Customer.Services/OrderService.cs

/impact --diff HEAD~1..HEAD
```

Under the hood, `/impact` calls the **`ace_impact_analyze`** tool and summarizes:

- Risk level and risk score
- Changed components
- **Direct** and **indirect** affected components
- Affected projects, APIs and tests
- An **evidence trail** explaining how each impact was derived

When the risk level is **High**, the command recommends following up with `ace_regression_scope` (i.e. the `/regression` command).

**CLI equivalent:**

```powershell
ace impact <path> <files...> [--diff <range>] [--json]
```

### 5.2 `/regression` — Recommend regression testing scope

**Arguments:**

| Argument | Required | Description |
|---|---|---|
| `--files <files...>` | Optional | Repo-relative paths of changed files |
| `--diff <range>` | Optional | Git diff range, e.g. `HEAD~1..HEAD` |

> **Note:** unlike `/impact` (positional files), `/regression` takes files via the **`--files`** flag.

**Examples:**

```text
/regression --files src/Customer.Services/CustomerService.cs

/regression --diff main..feature/orders
```

`/regression` calls the **`ace_regression_scope`** tool and reports:

- Risk level
- **Recommended scope** — e.g. "run affected unit tests" vs. "full regression"
- Impacted components
- Affected tests
- Additional notes

**CLI equivalent:**

```powershell
ace regression <path> [--files <files...>] [--diff <range>] [--json]
```

### Changed-file resolution precedence

For both commands, changed files are resolved in this order:

1. **`--diff` range** wins
2. Then **explicit files**
3. Then the **git working tree** (requires `enableGitAnalysis: true`)

---

## 6. Skills (auto-triggered by the agent)

Skills are knowledge bundles the agent applies automatically when your question matches a scenario — no slash command needed.

### 6.1 `change-impact`

**When it triggers:** before reviewing, committing, or merging code. Example prompts:

- *"What does changing `CustomerService` affect?"*
- *"Is this change risky?"*
- *"Which tests should I run for my changes?"*

**Workflow:** `ace_impact_analyze` → optionally `ace_tests_affected` / `ace_regression_scope`.

### 6.2 `repository-context`

**When it triggers:** when starting work in an unfamiliar repository, locating a symbol, or understanding how code fits together. Example prompts:

- *"Give me an overview of this repository."*
- *"Where is order validation handled?"*
- *"What depends on `ICustomerRepository`?"*

**Workflow:**

1. `ace_repository_analyze` — once per repository/session (discovery + index build/refresh)
2. `ace_context_get` with your query — returns context across **7 prioritized tiers**: direct code → dependencies → impacted → tests → config → architecture → repository context
3. Complementary tools as needed: `ace_code_search`, `ace_dependencies_get`, `ace_graph_query`, `ace_status`

ACE indexes **lazily on first use** and updates **incrementally** — no manual warm-up.

---

## 7. MCP Tools Reference

ACE exposes **12 MCP tools**. All tools require an **absolute `repositoryPath`**. Errors are returned as:

```json
{ "error": { "code": "...", "message": "..." } }
```

Error codes: `path_security`, `repository_not_found`, `invalid_argument`, `internal_error`.

### Change-set analysis tools

These share a common parameter set:

| Parameter | Type | Notes |
|---|---|---|
| `repositoryPath` | string | **Required**, absolute path |
| `changedFiles` | string[] | Optional, repo-relative |
| `useGitWorkingTree` | bool | Default `false`; requires `enableGitAnalysis` |
| `gitDiffRange` | string | Optional; requires `enableGitAnalysis` |

| Tool | Description |
|---|---|
| `ace_impact_analyze` | Changed/affected components, affected projects/APIs/tests, merged risk level/score, evidence trail |
| `ace_risk_analyze` | Deterministic risk level + 0–100 risk score with weighted factors |
| `ace_tests_affected` | Affected tests, each with a selection reason |
| `ace_regression_scope` | Recommended regression testing scope |
| `ace_architecture_analyze` | Architecture check vs. layering rules (takes `repositoryPath` only; returns an empty list when architecture analysis is disabled) |

### Context, graph and repository tools

| Tool | Parameters | Description |
|---|---|---|
| `ace_context_get` | `repositoryPath`, `query`, `maxItems` (default 50) | 7-tier prioritized context for a query |
| `ace_code_search` | `repositoryPath`, `query` | Case-insensitive symbol search |
| `ace_dependencies_get` | `repositoryPath`, `symbol` | Outgoing dependencies of a symbol |
| `ace_graph_build` | `repositoryPath` | Force rebuild of the code graph |
| `ace_graph_query` | `repositoryPath`, `nodeId`, `edgeTypes`?, `direction` (`Incoming`/`Outgoing`/`Both`, default `Both`) | Graph neighbors of a node |
| `ace_repository_analyze` | `repositoryPath` | Discovery + index build/refresh; returns repo context (file counts, languages, frameworks, build systems, test projects) |
| `ace_status` | `repositoryPath` | Engine/index health: `apiVersion`, `indexed`, `fileCount`, `nodeCount`, `edgeCount`, `stale`, `failedFiles`, etc. |

**`ace_graph_query` details:**

- `nodeId` format: `Project:Namespace.Type` (e.g. `Customer.Services:Customer.Services.CustomerService`)
- Edge types: `Contains`, `References`, `Calls`, `Implements`, `Inherits`, `DependsOn`, `Uses`, `Tests`, `Exposes`, `Configures`, `Reads`, `Writes`

---

## 8. CLI Reference (`ace.exe`)

The CLI provides the **same intelligence** as the MCP server, for terminal use. The executable is published at `dist\win-x64\ace.exe`.

Every verb takes a `path` argument (the repository path) and supports `--json` for machine-readable output.

| Verb | Description | Example |
|---|---|---|
| `init` | Initialize ACE in a repository | `ace init C:\repos\SampleRepo` |
| `index` | Build/refresh the repository index | `ace index C:\repos\SampleRepo` |
| `status` | Show engine/index health | `ace status C:\repos\SampleRepo --json` |
| `analyze` | Analyze repository structure | `ace analyze C:\repos\SampleRepo` |
| `impact` | Change impact analysis | `ace impact C:\repos\SampleRepo src/Customer.Services/CustomerService.cs --diff HEAD~1..HEAD` |
| `graph query` | Query the code graph | `ace graph query C:\repos\SampleRepo --node "Customer.Services:Customer.Services.CustomerService"` |
| `context` | Get prioritized context for a query | `ace context C:\repos\SampleRepo --query "customer tier pricing"` |
| `tests` | List affected tests | `ace tests C:\repos\SampleRepo --files src/Customer.Services/CustomerService.cs` |
| `regression` | Recommend regression scope | `ace regression C:\repos\SampleRepo --files src/Customer.Services/CustomerService.cs --json` |

> Note: MCP tools require an **absolute** `repositoryPath`; the CLI `path` argument serves the same purpose.

---

## 9. Troubleshooting / FAQ

**Q: `/impact` and `/regression` don't pick up my uncommitted changes.**
A: Git working-tree analysis is **off by default**. Set `"enableGitAnalysis": true` in the `"ace"` section of `ace.json`, or pass explicit file lists instead.

**Q: I get an error when passing a relative repository path.**
A: All MCP tools require an **absolute** `repositoryPath`. Relative paths are rejected (typically as `invalid_argument` or `path_security`).

**Q: `ace_architecture_analyze` returns an empty list.**
A: Architecture analysis is likely disabled. Check `enableArchitectureAnalysis` in `ace.json` (default is `true`) or the `ACE__enableArchitectureAnalysis` environment variable. When disabled, the tool intentionally returns an empty list rather than an error.

**Q: I reinstalled/updated the plugin and something is off.**
A: Uninstalling may leave folders under `~/.qoder/plugins/custom` and `~/.qoder/plugins/cache/local`. Delete those leftover folders manually before reinstalling.

**Q: The plugin doesn't work on my machine.**
A: The current package is **Windows x64 only**. Verify you're running Windows on x64 hardware.

**Q: What's the relationship between the MCP server and the CLI?**
A: Both are thin adapters over the same `Ace.Core` engine (Engine First design), so answers are identical regardless of how you ask.

---

## 10. Glossary

| Term | Meaning |
|---|---|
| **ACE** | Agent Context Engine — the local-first developer intelligence platform |
| **Risk score** | A deterministic 0–100 score computed from weighted risk factors, accompanied by a risk level (e.g. Low / Medium / High) |
| **Evidence trail** | The chain of graph relationships explaining an impact, formatted as `source --relationship--> target` |
| **Direct affected** | Components reached by a single relationship hop from a changed component |
| **Indirect affected** | Components reached transitively through multiple relationship hops |
| **Heuristic edges** | Graph edges inferred with uncertainty, carrying a **confidence < 1.0**; edges from exact code analysis carry confidence 1.0 |
| **Regression scope** | The recommended testing strategy for a change (e.g. run affected unit tests vs. full regression) |
| **7-tier context** | The prioritization order used by `ace_context_get`: direct code → dependencies → impacted → tests → config → architecture → repository context |
