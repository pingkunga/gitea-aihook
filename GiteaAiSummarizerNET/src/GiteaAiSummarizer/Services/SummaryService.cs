using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GiteaAiSummarizer.Models;
using Scriban;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace GiteaAiSummarizer.Services;

public class SummaryService(
    GiteaApiClient gitea,
    [FromKeyedServices("GiteaSummarizerAgent")] AIAgent agent,
    DiffProcessor diffProcessor,
    IConfiguration config,
    ILogger<SummaryService> logger
)
{
    private const string SummaryCommentMarker = "<!-- gitea-ai-summarizer:pr-summary -->";
    private readonly string _templatePath = Path.Combine(
        AppContext.BaseDirectory,
        "Templates",
        "default-prompt.txt"
    );
    private readonly string _statusContext = config["Gitea:StatusContext"] ?? "ai/pr-summary";
    private readonly bool _includeSkillOutputs = config.GetValue("Summary:IncludeSkillOutputs", false);

    /// <summary>Owner/repo the model needs for gitea-tools calls, plus the tool results a run collects.</summary>
    private sealed record RunContext(string Owner, string Repo, PullRequest Pr)
    {
        public List<ToolResult> ToolResults { get; } = new();
    }

    private sealed record ToolResult(string Stage, string Name, string Arguments, string Result);

    public async Task ProcessAsync(GiteaPayload payload, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var repo = payload.Repository.FullName;
        var pr = payload.PullRequest;
        string? commitSha = null;

        var parts = repo.Split('/', 2);
        if (parts.Length != 2)
        {
            logger.LogError("Cannot parse owner/repo from '{FullName}'", repo);
            return;
        }
        var (owner, repoName) = (parts[0], parts[1]);

        logger.LogInformation(
            "Processing PR #{Number} in {Repo} via Microsoft AI Agent",
            pr.Number,
            repo
        );

        try
        {
            if (string.IsNullOrWhiteSpace(pr.Head.Sha))
            {
                pr = await gitea.GetPullRequestAsync(owner, repoName, pr.Number, ct);
            }

            commitSha = pr.Head.Sha;
            if (string.IsNullOrWhiteSpace(commitSha))
            {
                throw new InvalidOperationException("Cannot resolve PR head SHA for commit status updates.");
            }

            await gitea.CreateCommitStatusAsync(
                owner,
                repoName,
                commitSha,
                _statusContext,
                "pending",
                "AI summary in progress",
                pr.HtmlUrl,
                ct
            );

            var diff = await gitea.GetDiffAsync(owner, repoName, pr.Number, ct);
            var strategy = diffProcessor.DetermineStrategy(diff);

            var run = new RunContext(owner, repoName, pr);

            string aiSummary = strategy switch
            {
                DiffStrategy.FullDiff => await SummarizeFullDiffAsync(run, diff, diff, ct),
                DiffStrategy.Chunked => await SummarizeChunkedAsync(run, diff, ct),
                // The model only sees the file-level summary, but scripts cost no model tokens, so
                // they still analyze the raw diff.
                DiffStrategy.FileLevelSummary
                or _
                    => await SummarizeFullDiffAsync(run, diffProcessor.BuildFileLevelSummary(diff), diff, ct)
            };

            // An empty summary is a failure, not a success with placeholder text. Bail before
            // posting anything so the commit status tells the truth.
            if (string.IsNullOrWhiteSpace(aiSummary))
            {
                logger.LogError(
                    "AI summary produced no output for PR #{Number} in {Repo} (strategy: {Strategy})",
                    pr.Number,
                    repo,
                    strategy
                );

                await gitea.CreateCommitStatusAsync(
                    owner,
                    repoName,
                    commitSha,
                    _statusContext,
                    "failure",
                    "AI summary produced no output",
                    pr.HtmlUrl,
                    ct
                );
                return;
            }

            sw.Stop();
            var provider = config["AI:ENGINE_TYPE"] ?? "AI";
            var comment = FormatComment(
                aiSummary,
                sw.Elapsed,
                provider,
                _includeSkillOutputs ? run.ToolResults : []
            );
            await UpsertSummaryCommentAsync(owner, repoName, pr.Number, comment, ct);

            await gitea.CreateCommitStatusAsync(
                owner,
                repoName,
                commitSha,
                _statusContext,
                "success",
                "AI summary posted",
                pr.HtmlUrl,
                ct
            );

            logger.LogInformation(
                "Comment posted to {Repo}#{PR} in {Elapsed:0.0}s",
                repo,
                pr.Number,
                sw.Elapsed.TotalSeconds
            );
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(commitSha))
            {
                try
                {
                    await gitea.CreateCommitStatusAsync(
                        owner,
                        repoName,
                        commitSha,
                        _statusContext,
                        "failure",
                        "AI summary failed",
                        pr.HtmlUrl,
                        ct
                    );
                }
                catch (Exception statusEx)
                {
                    logger.LogError(
                        statusEx,
                        "Failed to update commit status for PR #{Number} in {Repo}",
                        pr.Number,
                        repo
                    );
                }
            }

            logger.LogError(ex, "Failed to process PR #{Number} in {Repo}", pr.Number, repo);
        }
    }

    private async Task UpsertSummaryCommentAsync(
        string owner,
        string repo,
        int prNumber,
        string comment,
        CancellationToken ct
    )
    {
        var comments = await gitea.GetIssueCommentsAsync(owner, repo, prNumber, ct);
        var existing = comments.LastOrDefault(x => x.Body.Contains(SummaryCommentMarker));

        if (existing is null)
        {
            await gitea.PostCommentAsync(owner, repo, prNumber, comment, ct);
            return;
        }

        await gitea.UpdateIssueCommentAsync(owner, repo, existing.Id, comment, ct);
    }

    private async Task<string> SummarizeFullDiffAsync(
        RunContext run,
        string diffContent,
        string scriptDiff,
        CancellationToken ct
    )
    {
        var prompt = await BuildPromptAsync(run, diffContent);
        var response = await RunAgentAsync(run, "full-diff summary", prompt, scriptDiff, ct);

        if (string.IsNullOrWhiteSpace(response.Text))
        {
            LogEmptyResponse("full-diff summary", response);
            return "";
        }

        return response.Text;
    }

    private async Task<string> SummarizeChunkedAsync(
        RunContext run,
        string diff,
        CancellationToken ct
    )
    {
        var chunks = diffProcessor.SplitByFile(diff);
        var prHeader = BuildPrHeader(run);
        var fileSummaries = new List<string>();

        foreach (var chunk in chunks)
        {
            // Each chunk runs in its own session and repeats the (small) PR header. A shared session
            // made chunk N carry chunks 1..N-1 plus every tool result — the very context load
            // chunking exists to avoid, and enough to overflow a small-context model.
            var chunkPrompt = $"{prHeader}\n\nNow summarize this file: {chunk.FileName}\n\n{chunk.Content}";

            var response = await RunAgentAsync(run, $"chunk '{chunk.FileName}'", chunkPrompt, chunk.Content, ct);

            // A turn that ends on tool calls yields no text, so collect each chunk's text here
            // rather than assuming the consolidation turn will find it.
            if (string.IsNullOrWhiteSpace(response.Text))
            {
                LogEmptyResponse($"chunk '{chunk.FileName}'", response);
                continue;
            }

            fileSummaries.Add($"### {chunk.FileName}\n\n{response.Text.Trim()}");
            logger.LogInformation("Summarized chunk for file: {FileName}", chunk.FileName);
        }

        if (fileSummaries.Count == 0)
        {
            logger.LogError(
                "No file summaries were produced for any of the {ChunkCount} chunks",
                chunks.Count
            );
            return "";
        }

        var joined = string.Join("\n\n", fileSummaries);

        // Consolidate in a fresh session seeded only with the per-file summaries. Scripts see the
        // whole diff here, since the model is now reasoning about the PR as a whole.
        var finalResponse = await RunAgentAsync(
            run,
            "chunked consolidation",
            "Consolidate these file summaries into one PR summary following the required format.\n\n"
                + prHeader + "\n\n"
                + joined,
            diff,
            ct
        );

        if (!string.IsNullOrWhiteSpace(finalResponse.Text))
            return finalResponse.Text;

        // Per-file summaries beat nothing when only the consolidation turn came back empty.
        LogEmptyResponse("chunked consolidation", finalResponse);
        return joined;
    }

    /// <summary>
    /// Runs one agent turn in a fresh, never-persisted session, with <paramref name="scriptDiff"/>
    /// exposed to file-based skill scripts, and records every tool call's result.
    /// </summary>
    private async Task<AgentResponse> RunAgentAsync(
        RunContext run,
        string stage,
        string prompt,
        string scriptDiff,
        CancellationToken ct
    )
    {
        var session = await agent.CreateSessionAsync(ct);
        SkillRunContext.CurrentDiff = scriptDiff;
        try
        {
            var response = await agent.RunAsync(prompt, session, cancellationToken: ct);
            CaptureToolResults(run, stage, response);
            return response;
        }
        finally
        {
            SkillRunContext.CurrentDiff = null;
        }
    }

    /// <summary>
    /// Pairs each function call in the run with its result (by call id) and logs it — covering
    /// file scripts (<c>run_skill_script</c>), <c>gitea-tools</c> scripts, <c>load_skill</c> and
    /// <c>read_skill_resource</c> — so what a skill actually returned is visible after the fact.
    /// </summary>
    private void CaptureToolResults(RunContext run, string stage, AgentResponse response)
    {
        var contents = response.Messages.SelectMany(m => m.Contents).ToList();
        var calls = contents
            .OfType<FunctionCallContent>()
            .GroupBy(c => c.CallId)
            .ToDictionary(g => g.Key, g => g.First());

        // One line per run, even when nothing was called — "0 tool calls" is the signal that the model
        // answered straight from the diff and no skill or script ran.
        var callNames = calls.Values.Select(c => c.Name).ToList();
        logger.Log(
            callNames.Count == 0 ? LogLevel.Warning : LogLevel.Information,
            "Agent run at {Stage}: {ToolCallCount} tool calls [{ToolNames}], finish reason {FinishReason}",
            stage,
            callNames.Count,
            string.Join(", ", callNames),
            response.FinishReason?.ToString() ?? "none"
        );

        foreach (var result in contents.OfType<FunctionResultContent>())
        {
            calls.TryGetValue(result.CallId, out var call);
            var name = call?.Name ?? "(unknown)";
            var args = call?.Arguments is { Count: > 0 } a
                ? JsonSerializer.Serialize(a)
                : "";
            var text = result.Exception is { } ex
                ? $"Error: {ex.Message}"
                : result.Result switch
                {
                    null => "",
                    string s => s,
                    JsonElement e when e.ValueKind == JsonValueKind.String => e.GetString() ?? "",
                    var o => JsonSerializer.Serialize(o)
                };

            run.ToolResults.Add(new ToolResult(stage, name, args, text));
            logger.LogInformation(
                "Tool Result at {Stage}: {ToolName}({Arguments}) → {Length} chars: {Result}",
                stage,
                name,
                SkillScriptRunner.Truncate(args, 300),
                text.Length,
                SkillScriptRunner.Truncate(text, 2048)
            );
        }
    }

    private static string BuildPrHeader(RunContext run) =>
        $"""
        Repository: {run.Owner}/{run.Repo} (owner: {run.Owner}, repo: {run.Repo})
        PR Number: {run.Pr.Number}
        PR URL: {run.Pr.HtmlUrl}
        PR Title: {run.Pr.Title}
        PR Description: {run.Pr.Body}
        Target Branch: {run.Pr.Base.Ref}
        Source Branch: {run.Pr.Head.Ref}
        """;

    /// <summary>
    /// Records what a run actually returned when it produced no text, so an empty summary can be
    /// traced to tool calls, approval requests, or a truncated response instead of vanishing.
    /// </summary>
    private void LogEmptyResponse(string stage, AgentResponse response)
    {
        var contents = response.Messages.SelectMany(m => m.Contents).ToList();
        var contentTypes = contents
            .GroupBy(c => c.GetType().Name)
            .Select(g => $"{g.Key}={g.Count()}");
        var functionCalls = contents.OfType<FunctionCallContent>().Select(c => c.Name).ToList();

        logger.LogWarning(
            "Agent run returned no text at {Stage}: {MessageCount} messages, contents [{ContentTypes}], "
                + "{FunctionCallCount} function calls [{FunctionNames}], finish reason {FinishReason}",
            stage,
            response.Messages.Count,
            string.Join(", ", contentTypes),
            functionCalls.Count,
            string.Join(", ", functionCalls.Distinct()),
            response.FinishReason?.ToString() ?? "none"
        );
    }

    private async Task<string> BuildPromptAsync(
        RunContext run,
        string diffContent,
        string? extraInstruction = null
    )
    {
        var templateText = await LoadTemplateAsync();
        var template = Template.Parse(templateText);
        var pr = run.Pr;

        return await template.RenderAsync(
            new
            {
                owner = run.Owner,
                repo = run.Repo,
                pr_number = pr.Number,
                pr_url = pr.HtmlUrl,
                pr_title = pr.Title,
                pr_body = pr.Body ?? string.Empty,
                base_branch = pr.Base.Ref,
                head_branch = pr.Head.Ref,
                diff_content = diffContent,
                extra_instruction = extraInstruction ?? string.Empty
            }
        );
    }

    private async Task<string> LoadTemplateAsync()
    {
        if (File.Exists(_templatePath))
            return await File.ReadAllTextAsync(_templatePath);
        return DefaultPromptTemplate;
    }

    private static string FormatComment(
        string summary,
        TimeSpan elapsed,
        string provider,
        IReadOnlyList<ToolResult> toolResults
    ) =>
        $"""
        {SummaryCommentMarker}

        🤖 **AI Summary** (powered by {provider})

        {summary}
        {FormatToolResults(toolResults)}
        ---
        <sub>Generated by Gitea AI Summarizer • took {elapsed.TotalSeconds:0.0}s</sub>
        """;

    private static string FormatToolResults(IReadOnlyList<ToolResult> toolResults)
    {
        if (toolResults.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine().AppendLine("<details><summary>🛠 Skill outputs</summary>").AppendLine();
        foreach (var r in toolResults)
        {
            sb.AppendLine($"**{r.Name}** <sub>({r.Stage})</sub>");
            if (r.Arguments.Length > 0)
                sb.AppendLine($"<sub>args: `{SkillScriptRunner.Truncate(r.Arguments, 200).Replace("`", "'")}`</sub>");
            sb.AppendLine().AppendLine("```text").AppendLine(SkillScriptRunner.Truncate(r.Result, 4000).Replace("```", "'''")).AppendLine("```").AppendLine();
        }
        sb.AppendLine("</details>");
        return sb.ToString();
    }

    private const string DefaultPromptTemplate = """
        {{ if extra_instruction != "" }}
        Additional instruction: {{ extra_instruction }}
        {{ end }}

        ---
        Repository: {{ owner }}/{{ repo }} (owner: {{ owner }}, repo: {{ repo }})
        PR Number: {{ pr_number }}
        PR URL: {{ pr_url }}
        PR Title: {{ pr_title }}
        PR Description: {{ pr_body }}
        Target Branch: {{ base_branch }}
        Source Branch: {{ head_branch }}

        Diff:
        {{ diff_content }}
        """;
}
