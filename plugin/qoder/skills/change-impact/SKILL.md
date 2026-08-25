---
name: change-impact
description: Use the ACE MCP tools to understand what a code change affects before reviewing, committing or merging it. Use when asked what a change impacts, whether a change is risky, or which tests to run.
---

# Skill: Change Impact Analysis

Use the ACE MCP tools to understand what a code change affects before reviewing,
committing or merging it. ACE provides deterministic, evidence-based analysis —
never guess impact when ACE can compute it.

## When to use

- Before committing or opening a review for modified files.
- When asked "what does changing X affect?" or "is this change risky?".
- When deciding which tests to run for a change set.
- When scoping regression testing for a diff range (e.g. `HEAD~1..HEAD`).

## How to use

1. Call `ace_impact_analyze` with:
   - `repositoryPath`: the repository root.
   - `changedFiles`: repository-relative paths of the changed files
     (from the working tree, a diff range, or the user's list).
2. Read the structured result: `riskLevel` / `riskScore`, `changedComponents`,
   `affectedComponents` (direct + indirect), `affectedProjects`, `affectedApis`,
   `affectedTests` and the `evidence` trail (`source --relationship--> target`).
3. If the change set needs test selection, call `ace_tests_affected` with the
   same inputs to get the affected test list with reasons.
4. If the user asks how much regression testing is needed, call
   `ace_regression_scope` — it combines impact + risk + affected tests into a
   recommended scope.

## Notes

- Heuristic edges carry `confidence` < 1.0 and `evidence`; prefer observed facts
  when summarizing results.
- If git is unavailable or the folder is not a repository, pass explicit file
  lists instead of diff ranges.
- Never implement impact logic yourself — always delegate to these tools.
