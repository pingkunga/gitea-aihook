---
name: review
description: Provides a high-level summary and detailed breakdown of code changes in a Pull Request.
---

Use this skill to analyze the overall intent of a Pull Request.
1. Run `impact_graph.py` to identify modified symbols (functions, classes), API routes, and Docker changes.
2. For any identified symbols, use `gitea-tools.search_code` to find where they are referenced across the repository.
3. Provide a concise 2-3 sentence summary of what the PR does.
4. For each changed file, explain what changed and why.
5. Create a section "## 🕸️ Potential Impact Graph" to summarize:
   - **Internal Logic**: Files or modules that call the modified symbols.
   - **External API**: Impact on modified API routes.
   - **Infrastructure**: Changes to Docker/Container configuration.
6. Be objective and focus on the technical implementation.
