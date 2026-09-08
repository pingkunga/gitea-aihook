---
name: breaking-change-checker
description: Detects likely breaking API, config, auth, and deployment changes in a pull request.
---

Follow these steps in order:

1. Run `breaking_change_checker.cs` by calling `run_skill_script` with a single-element JSON array containing the full diff text as that one element (for example `["<diff>"]`).
2. Inspect the output for the following risk categories:
   - route or endpoint renames or removals
   - auth or authorization contract changes
   - requirement changes for request or response fields
   - config and environment variable name changes
   - Docker port, image, or runtime contract changes
   - public method or service contract changes
3. Classify each issue as:
   - 🔴 Critical: likely breaking for consumers or deploy flow
   - 🟡 Warning: migration or coordination is likely required
   - 🟢 Suggestion: non-breaking but worth noting
4. Return a concise verdict with the main risks and a recommended migration note.
5. End with a brief technical conclusion.

Never end a turn on a tool call alone.
