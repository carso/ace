---
name: impact
description: Analyze change impact, risk and affected components/tests for the current repository using the ACE MCP tools.
---

# Command: /impact

Analyze the impact of a set of changed files (or the git working tree / a diff
range) in the current repository.

## Arguments

- `<files...>` (optional): repository-relative changed files.
- `--diff <range>` (optional): e.g. `HEAD~1..HEAD`.

When neither is given, the git working tree changes are used.

## Behavior

Invoke the `ace_impact_analyze` MCP tool with:

- `repositoryPath`: the current workspace root.
- `changedFiles`: the argument files, the files from `git diff --name-only <range>`
  when `--diff` is given, or the working-tree changes otherwise.

Then summarize the result for the user: risk level/score, changed components,
direct and indirect affected components, affected projects/APIs/tests and the
evidence trail. If the risk is High, recommend calling `ace_regression_scope`.

## CLI equivalent

```text
ace impact <path> <files...> [--diff <range>] [--json]
```
