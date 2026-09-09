---
name: review
description: Provides a high-level summary and detailed breakdown of code changes in a Pull Request.
---

Follow these steps in order to analyze the Pull Request:

1. Run `impact_graph.py` by calling `run_skill_script` with a single-element JSON array containing the entire diff text as that one element (e.g. `["<diff>"]`) — there is no named `diff` field, the script takes a plain array. This identifies modified symbols, API routes, and Docker changes. Skip this step when the input is the diff of a single file — read the diff directly instead.
2. Call `gitea-tools.search_code` for at most 5 of the most significant symbols identified. Every call costs a round trip, and a turn that spends all of them on tool calls returns no summary at all.
3. Provide a concise 2-3 sentence summary of what the PR does based on the gathered context.
4. Explain what changed and why for each changed file.
5. Add a `## Potential Impact Graph` section describing internal logic, external API, and infrastructure impact.
6. Finalize with objective technical observations.

Always end your turn with the written summary. Never end a turn on a tool call alone.
