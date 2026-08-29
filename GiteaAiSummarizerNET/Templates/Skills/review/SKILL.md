---
name: review
description: Provides a high-level summary and detailed breakdown of code changes in a Pull Request.
---

YOU MUST FOLLOW THE STEPS BELOW IN ORDER TO ANALYZE THE PULL REQUEST:

1. STEP 1: Run `impact_graph.py` by passing the ACTUAL git diff text (not a filename) as the `diff` argument. This script identifies modified symbols, API routes, and Docker changes.
2. STEP 2: YOU MUST use every symbol identified in the output of `impact_graph.py` to call `gitea-tools.search_code`. This is mandatory to find where these symbols are referenced across the repository for impact analysis.
3. STEP 3: Provide a concise 2-3 sentence summary of what the PR does based on the gathered context.
4. STEP 4: For each changed file, explain what changed and why.
5. STEP 5: Create a section "## 🕸️ Potential Impact Graph" to summarize:
   - **Internal Logic**: Files or modules that call the modified symbols (found via `gitea-tools.search_code`).
   - **External API**: Impact on modified API routes.
   - **Infrastructure**: Changes to Docker/Container configuration.
6. STEP 6: Finalize the review with objective technical observations.
