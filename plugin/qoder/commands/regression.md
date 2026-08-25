---
name: regression
description: Recommend regression testing scope for a change set using the ACE MCP tools.
---

# Command: /regression

Recommend the regression testing scope for a change set in the current
repository.

## Arguments

- `--files <files...>` (optional): repository-relative changed files.
- `--diff <range>` (optional): e.g. `HEAD~1..HEAD`.

When neither is given, the git working tree changes are used.

## Behavior

Invoke the `ace_regression_scope` MCP tool with:

- `repositoryPath`: the current workspace root.
- `changedFiles`: the `--files` values, the files from `git diff --name-only
  <range>` when `--diff` is given, or the working-tree changes otherwise.

Then report to the user: risk level, the recommended scope (e.g. "run affected
unit tests" vs "full regression"), potentially impacted components, the
affected tests and any notes. Optionally follow up with `ace_tests_affected`
for the precise test list with reasons.

## CLI equivalent

```text
ace regression <path> [--files <files...>] [--diff <range>] [--json]
```
