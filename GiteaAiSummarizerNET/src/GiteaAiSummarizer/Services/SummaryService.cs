using System.Diagnostics;
using GiteaAiSummarizer.Models;
using Scriban;
using Microsoft.Agents.AI;

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

            // One session per PR run, never persisted — keeps the service stateless
            // across invocations while giving a single run's chunk calls shared context.
            var session = await agent.CreateSessionAsync(ct);

            string aiSummary = strategy switch
            {
                DiffStrategy.FullDiff => await SummarizeFullDiffAsync(pr, diff, session, ct),
                DiffStrategy.Chunked => await SummarizeChunkedAsync(pr, diff, session, ct),
                DiffStrategy.FileLevelSummary
                or _
                    => await SummarizeFullDiffAsync(
                        pr,
                        diffProcessor.BuildFileLevelSummary(diff),
                        session,
                        ct
                    )
            };

            sw.Stop();
            var provider = config["AI:ENGINE_TYPE"] ?? "AI";
            var comment = FormatComment(aiSummary, sw.Elapsed, provider);
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
        PullRequest pr,
        string diffContent,
        AgentSession session,
        CancellationToken ct
    )
    {
        var prompt = await BuildPromptAsync(pr, diffContent);
        var response = await agent.RunAsync(prompt, session, cancellationToken: ct);
        return string.IsNullOrWhiteSpace(response.Text) ? "No summary found." : response.Text;
    }

    private async Task<string> SummarizeChunkedAsync(
        PullRequest pr,
        string diff,
        AgentSession session,
        CancellationToken ct
    )
    {
        var chunks = diffProcessor.SplitByFile(diff);

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            // Only the first turn needs PR metadata — it stays in the shared session's
            // history for every later turn, including the consolidation call below.
            var chunkPrompt = i == 0
                ? $"PR Title: {pr.Title}\nPR Description: {pr.Body}\nTarget Branch: {pr.Base.Ref}\nSource Branch: {pr.Head.Ref}\n\nNow summarize this file: {chunk.FileName}\n\n{chunk.Content}"
                : $"Now summarize this file: {chunk.FileName}\n\n{chunk.Content}";

            await agent.RunAsync(chunkPrompt, session, cancellationToken: ct);
            logger.LogInformation("Summarized chunk for file: {FileName}", chunk.FileName);
        }

        var finalResponse = await agent.RunAsync(
            "Consolidate the file summaries above into one PR summary following the required format.",
            session,
            cancellationToken: ct
        );
        return string.IsNullOrWhiteSpace(finalResponse.Text) ? "No summary found." : finalResponse.Text;
    }

    private async Task<string> BuildPromptAsync(
        PullRequest pr,
        string diffContent,
        string? extraInstruction = null
    )
    {
        var templateText = await LoadTemplateAsync();
        var template = Template.Parse(templateText);

        return await template.RenderAsync(
            new
            {
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

    private static string FormatComment(string summary, TimeSpan elapsed, string provider) =>
        $"""
        {SummaryCommentMarker}

        🤖 **AI Summary** (powered by {provider})

        {summary}

        ---
        <sub>Generated by Gitea AI Summarizer • took {elapsed.TotalSeconds:0.0}s</sub>
        """;

    private const string DefaultPromptTemplate = """
        {{ if extra_instruction != "" }}
        Additional instruction: {{ extra_instruction }}
        {{ end }}

        ---
        PR Title: {{ pr_title }}
        PR Description: {{ pr_body }}
        Target Branch: {{ base_branch }}
        Source Branch: {{ head_branch }}

        Diff:
        {{ diff_content }}
        """;
}
