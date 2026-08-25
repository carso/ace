# ACE — Agent Context Engine

ACE is a cross-platform developer intelligence platform that gives AI coding agents
structured, repository-aware context: repository structure, code relationships,
dependencies, change impact, affected tests, architecture checks, and regression
scope — all with deterministic evidence.

## Author

- **Author:** Carso Leong
- **Location:** Kuala Lumpur


## Architecture

```text
AI Clients (Qoder, VS Code, Claude, Gemini, Codex, ...)
        │  MCP (Model Context Protocol)
        ▼
ACE MCP Server  ──►  ACE Engine (Ace.Core)  ──►  Your Codebase
                     (index, graph, impact,
                      risk, tests, architecture)
```

The intelligence lives entirely in **Ace.Core** (Engine First). The MCP server and
the CLI are thin adapters over the same engine, so every AI client gets identical
answers. ACE is local-first: repositories are indexed under `<repo>/.ace` and no
source code leaves the machine.

## Projects

| Project | Purpose |
|---|---|
| `src/Ace.Core` | Core engine: discovery, incremental index, parsing, code graph, analysis engines |
| `src/Ace.Mcp.Server` | MCP server (stdio transport) exposing ACE tools to AI agents |
| `src/Ace.Cli` | `ace` command-line interface (init, index, status, impact, ...) |
| `tests/Ace.Core.Tests` | Unit tests for the core engine |
| `tests/Ace.Mcp.Integration.Tests` | Integration tests against the real MCP server |

## Build

```powershell
dotnet build Ace.sln -c Release
dotnet test Ace.sln -c Release
```

## Plugin User Guide

See [ACE Plugin User Guide](docs/ACE_Plugin_User_Guide.md) — covers installing the
`ace-qoder.zip` plugin, configuration (`ace.json`), the `/impact` and `/regression`
commands, the change-impact and repository-context skills, the MCP tool reference,
the CLI reference, and troubleshooting.


