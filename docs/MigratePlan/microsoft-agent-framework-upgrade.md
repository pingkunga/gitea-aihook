# Migration Plan: Microsoft Agent Framework Upgrade

**Scope:** `GiteaAiSummarizerNET/` (the sole .NET project in this repo)
**Date:** 2026-09-05

## Context

`GiteaAiSummarizerNET/GiteaAiSummarizer.csproj` already bets on the **Microsoft Agent Framework** (`Microsoft.Agents.AI` / `Microsoft.Agents.AI.Hosting`) as its AI layer — but the reference is pinned to a stale pre-GA preview build, **`1.1.0-preview.260410.1`** (April 2026). Microsoft shipped Agent Framework 1.0 GA on 2026-04-03 and has since iterated to **`1.20.0`** (stable, published 2026-08-31) — the project is ~5 months and ~19 minor versions behind.

Worse, the framework isn't actually doing anything today. `Program.cs` registers an agent via `builder.AddAIAgent("GiteaSummarizerAgent", ...)`, but nothing ever injects or calls it — `SummaryService` talks to the raw `IChatClient` directly (`chatClient.GetResponseAsync(...)`), bypassing the framework entirely. It's dead scaffolding.

**Goal:** bump to the current GA line, and make the app actually run its summarization calls through the registered `ChatClientAgent`/`AgentSession` API instead of the raw chat client — giving the chunked-diff summarization flow real multi-turn context (today each per-file chunk call is a fully independent, stateless LLM call that re-pastes the whole instruction/format contract every time), built-in OpenTelemetry instrumentation, and a real foundation for future tool-calling (CLAUDE.md §11 already envisions inline review comments / Review Hints tooling, which needs function-calling support the unused `tools: [...]` comment in `Program.cs` was clearly headed toward).

No Semantic Kernel / AutoGen involved anywhere — those are unrelated legacy products this repo never used. Also note: this repo's `Microsoft.Agents.*` reference is **not** the same product as the old Microsoft 365 Agents/Bot Framework SDK (`Microsoft.Agents.Authentication`/`Core`/`Hosting.AspNetCore`, still present as dead commented-out lines in the `.csproj`) — the two share a package-name prefix but are unrelated.

## What's new since the pinned preview (1.1.0-preview.260410.1 → 1.20.0 GA)

- Agent Framework reached 1.0 GA (2026-04-03), unifying Semantic Kernel's enterprise foundations with AutoGen's orchestration model into one stable, supported API surface.
- `ChatClientAgentOptions.Instructions` was removed in favor of passing `instructions` directly to the `ChatClientAgent` constructor — this repo's code already does this, so no change needed there.
- `RunAsync`'s `thread` parameter was renamed to `session` (type `AgentSession`) — not a break for this repo today since the registered agent is never called, but it's the API this migration will now start using.
- `Microsoft.Extensions.AI` floor raised from a 9.x preview to the stable `10.4.1` — already satisfied here.
- Agents are stateless; conversation state now lives entirely in `AgentSession`, created via `agent.CreateSessionAsync(ct)`.
- OpenTelemetry instrumentation ships enabled by default.
- Graph-based multi-agent workflows (sequential/concurrent/handoff/group-chat), progressive/dynamic tool exposure, and broader multi-provider/interop support (A2A, MCP) landed as the framework matured past 1.0 — not needed for this migration, but relevant context for the CLAUDE.md §11 roadmap (Code Review Mode, multi-LLM support).

## Package changes — `GiteaAiSummarizerNET/GiteaAiSummarizer.csproj`

- Bump `Microsoft.Agents.AI.Hosting` → `1.20.0-preview.260831.1` (current latest; this package has **never** shipped a non-preview build — that's normal, the *core* `Microsoft.Agents.AI` package is the GA-stable one).
- Replace the stale commented-out `<!-- <PackageReference Include="Microsoft.Agents.AI" Version="1.1.0" /> -->` with an **active, explicit** `<PackageReference Include="Microsoft.Agents.AI" Version="1.20.0" />` — pin it directly instead of relying on `.Hosting`'s transitive reference, so the two packages can't silently drift apart again.
- Leave `Microsoft.Extensions.AI` / `Microsoft.Extensions.AI.OpenAI` at `10.4.1` — this already satisfies the GA floor Agent Framework 1.20.0 requires. Only bump further if `dotnet restore` reports a higher minimum.
- Delete the two unrelated commented-out blocks so the file stops conflating two different "Microsoft.Agents.*" products:
  - `Microsoft.Agents.Authentication` / `Microsoft.Agents.Core` / `Microsoft.Agents.Hosting.AspNetCore` `1.4.83` (old Microsoft 365 Agents/Bot Framework SDK — different product)
  - The now-redundant commented `Microsoft.Agents.AI 1.1.0` line (superseded by the new active reference above)

## `Program.cs`

- Keep the same `AddAIAgent(name, Func<IServiceProvider,string,AIAgent>, ServiceLifetime)` factory-delegate overload — this shape is stable in 1.20.0's `HostApplicationBuilderAgentExtensions.AddAIAgent`. No signature change needed.
- Consolidate the persona instructions: fold the "You are a senior code reviewer... ## Summary / ## Changes Breakdown / ## Impact Analysis / ## Review Hints" format contract (currently living in `Templates/default-prompt.txt`) into the `instructions:` string passed to `ChatClientAgent` here, alongside the existing short persona blurb. This is the model's durable job description — it shouldn't be re-sent as part of every per-call user prompt.
- Delete the dead debris: the commented-out `.Build(sp => ...)` fluent-builder block (~lines 56-67) and the commented `tools: [...]` placeholder — both are leftovers from an earlier attempt. (A single-line `// TODO: pass AITool[] here once Review Hints needs code inspection` is fine to keep as a breadcrumb if useful, but the multi-line commented block should go.)
- No DI wiring change needed beyond this — `AddAIAgent` already registers the resulting `AIAgent` as a **keyed singleton** (keyed by the `name` string, `"GiteaSummarizerAgent"`), confirmed against the current API docs.

## `Services/SummaryService.cs` — the real refactor

- Constructor: replace `IChatClient chatClient` with `[FromKeyedServices("GiteaSummarizerAgent")] AIAgent agent`.
- `ProcessAsync`: right after resolving the PR (before the diff/strategy branch), create one session for this run:
  ```csharp
  var session = await agent.CreateSessionAsync(ct);
  ```
  Pass `session` into whichever summarize method runs. It's created fresh per webhook invocation and never persisted — this keeps the service stateless across invocations (matching CLAUDE.md's "ไม่เก็บ source code ไว้ในระบบ (stateless)" non-goal) while still getting multi-turn context *within* a single PR's processing.
- `SummarizeFullDiffAsync`: replace `chatClient.GetResponseAsync(prompt, ct)` with:
  ```csharp
  var response = await agent.RunAsync(prompt, session, cancellationToken: ct);
  return response.Text; // confirm exact member at build time (AgentRunResponse) — fall back to a Messages-based extraction if .Text isn't present, mirroring today's pattern
  ```
- `SummarizeChunkedAsync`: reuse the **same** `session` across every per-file chunk call and the final consolidation call — this is the concrete new capability being adopted:
  - Per-chunk prompt shrinks to just `"Now summarize this file: {chunk.FileName}\n\n{chunk.Content}"` — no need to re-paste the persona/section-format contract (now agent-level) or PR metadata on every single chunk turn.
  - Consolidation prompt shrinks to `"Consolidate the file summaries above into one PR summary following the required format."` — the partial summaries are already in the session's conversation history, so there's no need to manually re-concatenate and re-paste them into the prompt as `partialSummaries` does today.
  - Each call: `await agent.RunAsync(chunkPrompt, session, cancellationToken: ct)`.
- `BuildPromptAsync` / `Templates/default-prompt.txt`: trim the template down to just the per-turn variable content (PR title/body/branches/diff + extra instruction) now that persona + format live in `Program.cs`'s `instructions:`. Keep `DefaultPromptTemplate` as the fallback constant, updated the same way.
- `FormatComment` / provider label: stop hardcoding `"Microsoft Agent"` — pass through the actual configured `AI:ENGINE_TYPE` (already available via `IConfiguration`, same key `ChatClientFactory` reads) so the posted comment honestly says "powered by Azure/OpenAI/Ollama/Gemini" instead of a fixed string.

## `Services/ChatClientFactory.cs`

- Drop the unused `using Microsoft.Agents.AI;` import.
- No functional changes — it keeps producing the raw `IChatClient`; `Program.cs` wraps whatever it returns in `ChatClientAgent`, so Azure/OpenAI/Ollama/Gemini support is preserved automatically (Agent Framework wraps `IChatClient`, it doesn't replace it).

## Impact summary

| Area | Impact |
|---|---|
| Package deps | 2 lines changed (`.Hosting` bump, new explicit `Microsoft.Agents.AI` pin), 2 stale commented blocks removed |
| `Program.cs` | Instructions consolidated, dead commented blocks removed — no DI shape change |
| `SummaryService.cs` | Constructor DI swap (`IChatClient` → keyed `AIAgent`), all 3 chat-call sites rewritten to `agent.RunAsync(..., session, ...)`, one new `CreateSessionAsync` call per `ProcessAsync` run |
| `ChatClientFactory.cs` | Unused import removed only |
| `Templates/default-prompt.txt` | Trimmed — persona/format contract moves to agent instructions |
| Behavior change | Chunked-diff summarization gains real multi-turn context (smaller per-chunk prompts, no manual re-pasting of partial summaries); PR comment "powered by" label becomes accurate instead of hardcoded |
| Multi-provider support | Unaffected — Agent Framework wraps `IChatClient`, doesn't replace it |
| Statelessness (CLAUDE.md non-goal) | Preserved — session lives only for the duration of one `ProcessAsync` call, never persisted |

## Verification

1. `dotnet restore` then `dotnet build` from `GiteaAiSummarizerNET/` — fix any compile errors the version bump surfaces.
2. Copy `appsettings.example.json` → `appsettings.json` (or `appsettings.Development.json`) with a real test Gitea instance + a cheap/local AI engine (Ollama is easiest for no API cost).
3. `dotnet run`, confirm `GET /health` responds.
4. Exercise the manual-trigger endpoint (`POST /api/v1/summarize/{owner}/{repo}/{prNumber}`, Bearer `AdminToken`) or the checked-in `.http` file against:
   - a small test PR (exercises `SummarizeFullDiffAsync`)
   - a larger multi-file PR (exercises `SummarizeChunkedAsync`'s shared-session path)
5. Confirm on the real PR: a correctly formatted bot comment appears with the right "powered by X" label; re-triggering updates the existing comment (marker-based upsert) instead of duplicating it.
