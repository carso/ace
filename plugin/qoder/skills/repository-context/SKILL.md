---
name: repository-context
description: Use the ACE MCP tools to ground in a repository before editing, refactoring or answering questions about it. Use when starting work in an unfamiliar repo, locating a symbol, or understanding how code fits together.
---

# Skill: Repository Context

Use the ACE MCP tools to ground yourself in a repository before editing,
refactoring or answering questions about it. ACE builds a code graph and ranks
context deterministically — prefer it over ad-hoc guessing.

## When to use

- When starting work in an unfamiliar repository or module.
- Before modifying a type/service: gather its dependencies, dependents and tests.
- When asked "where is X?" or "how does X fit into this codebase?".
- When a task spans multiple layers (API → service → repository).

## How to use

1. Call `ace_repository_analyze` with `repositoryPath` to get the structured
   repository context: file/source counts, languages, frameworks, projects and
   test projects. Do this once per repository per session.
2. For a focused task, call `ace_context_get` with `repositoryPath` and a
   `query` (symbol, file or topic). It returns prioritized items across 7 tiers
   (direct code → dependencies → impacted → tests → config → architecture →
   repository context), each with a reason for inclusion.
3. Complementary lookups:
   - `ace_code_search`: case-insensitive symbol search (name/substring).
   - `ace_dependencies_get`: outgoing dependencies of a symbol.
   - `ace_graph_query`: neighbors of a graph node id (with direction).
   - `ace_status`: index/graph health (versions, staleness, failed files).

## Notes

- ACE indexes lazily on first use per repository and updates incrementally;
  no manual warm-up is required.
- Never re-implement context ranking yourself — always delegate to these tools.
