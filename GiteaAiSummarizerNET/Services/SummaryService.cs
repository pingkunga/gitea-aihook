using System.Diagnostics;
using System.Text;
using GiteaAiSummarizer.Models;
using Scriban;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;

namespace GiteaAiSummarizer.Services;

public class SummaryService(
    GiteaApiClient gitea,
    IChatClient chatClient,
    DiffProcessor diffProcessor,
    ILogger<SummaryService> logger
)
{
    private readonly string _templatePath = Path.Combine(
        AppContext.BaseDirectory,
        "Templates",
        "default-prompt.txt"
    );

    public async Task ProcessAsync(GiteaPayload payload, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var repo = payload.Repository.FullName;
        var pr = payload.PullRequest;

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
            var diff = await gitea.GetDiffAsync(owner, repoName, pr.Number, ct);
            var strategy = diffProcessor.DetermineStrategy(diff);

            string aiSummary = strategy switch
            {
                DiffStrategy.FullDiff => await SummarizeFullDiffAsync(pr, diff, ct),
                DiffStrategy.Chunked => await SummarizeChunkedAsync(pr, diff, ct),
                DiffStrategy.FileLevelSummary
                or _
                    => await SummarizeFullDiffAsync(
                        pr,
                        diffProcessor.BuildFileLevelSummary(diff),
                        ct
                    )
            };

            sw.Stop();
            var comment = FormatComment(aiSummary, sw.Elapsed, "Microsoft Agent");
            await gitea.PostCommentAsync(owner, repoName, pr.Number, comment, ct);

            logger.LogInformation(
                "Comment posted to {Repo}#{PR} in {Elapsed:0.0}s",
                repo,
                pr.Number,
                sw.Elapsed.TotalSeconds
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process PR #{Number} in {Repo}", pr.Number, repo);
        }
    }

    private async Task<string> SummarizeFullDiffAsync(
        PullRequest pr,
        string diffContent,
        CancellationToken ct
    )
    {
        var prompt = await BuildPromptAsync(pr, diffContent);
        var response = await chatClient.GetResponseAsync(prompt, cancellationToken: ct);
        return response.Messages?.FirstOrDefault()?.Text ?? "No summary found.";
    }

    private async Task<string> SummarizeChunkedAsync(
        PullRequest pr,
        string diff,
        CancellationToken ct
    )
    {
        var chunks = diffProcessor.SplitByFile(diff);
        var partialSummaries = new StringBuilder();

        foreach (var chunk in chunks)
        {
            var chunkPrompt = await BuildPromptAsync(
                pr,
                chunk.Content,
                $"Focus only on the file: {chunk.FileName}. Provide a brief summary of changes."
            );

            var partialResponse = await chatClient.GetResponseAsync(
                chunkPrompt,
                cancellationToken: ct
            );
            var partial = partialResponse.Messages?.FirstOrDefault()?.Text ?? string.Empty;

            partialSummaries.AppendLine($"### `{chunk.FileName}`");
            partialSummaries.AppendLine(partial);
            partialSummaries.AppendLine();
            logger.LogInformation("Summarized chunk for file: {FileName}", chunk.FileName);
        }

        var consolidationPrompt = await BuildPromptAsync(
            pr,
            $"[Chunked summaries per file]\n{partialSummaries}",
            "Consolidate the above per-file summaries into a cohesive PR summary with all required sections."
        );

        var finalResponse = await chatClient.GetResponseAsync(
            consolidationPrompt,
            cancellationToken: ct
        );
        return finalResponse.Messages?.FirstOrDefault()?.Text ?? "No summary found.";
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
        🤖 **AI Summary** (powered by {provider})

        {summary}

        ---
        <sub>Generated by Gitea AI Summarizer • took {elapsed.TotalSeconds:0.0}s</sub>
        """;

    private const string DefaultPromptTemplate = """
        You are a senior code reviewer. Analyze the following Pull Request diff and provide:

        ## 🔍 Summary
        A concise 2-3 sentence summary of what this PR does.

        ## 📁 Changes Breakdown
        For each changed file, briefly explain what changed and why.

        ## ⚠️ Impact Analysis
        - Breaking changes
        - Performance implications
        - Security concerns

        ## 💡 Review Hints
        Specific lines or patterns the reviewer should pay extra attention to.

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
