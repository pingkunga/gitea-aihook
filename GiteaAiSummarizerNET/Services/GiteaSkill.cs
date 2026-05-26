using System.ComponentModel;
using System.Text.Json;
using Microsoft.Agents.AI;

namespace GiteaAiSummarizer.Services;

public sealed class GiteaSkill : AgentClassSkill<GiteaSkill>
{
    private readonly GiteaApiClient _gitea;

    public GiteaSkill(GiteaApiClient gitea)
    {
        _gitea = gitea;
    }

    public override AgentSkillFrontmatter Frontmatter { get; } = new(
        "gitea-tools",
        "Provides access to Gitea repository information including issues, comments, and file history.");

    protected override string Instructions => """
        Use this skill to gather additional context from the Gitea repository.
        1. When a PR mentions an issue (e.g., #123), use 'get_issue' to read the issue description.
        2. Use 'get_issue_comments' to understand the conversation around a specific task.
        """;

    [AgentSkillScript("get_issue")]
    [Description("Fetches detailed information about a Gitea issue or pull request by its number.")]
    public async Task<string> GetIssueAsync(
        [Description("The owner of the repository.")] string owner,
        [Description("The name of the repository.")] string repo,
        [Description("The issue or pull request number.")] int number,
        CancellationToken ct = default)
    {
        // Gitea API treats PRs and Issues similarly for metadata
        var url = $"{_gitea.BaseUrl}/api/v1/repos/{owner}/{repo}/issues/{number}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        _gitea.AddHeaders(request);

        var response = await _gitea.HttpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return $"Error: {response.StatusCode}";

        var content = await response.Content.ReadAsStringAsync(ct);
        return content;
    }

    [AgentSkillScript("get_issue_comments")]
    [Description("Fetches comments for a specific Gitea issue or pull request.")]
    public async Task<string> GetIssueCommentsAsync(
        [Description("The owner of the repository.")] string owner,
        [Description("The name of the repository.")] string repo,
        [Description("The issue or pull request number.")] int number,
        CancellationToken ct = default)
    {
        var comments = await _gitea.GetIssueCommentsAsync(owner, repo, number, ct);
        return JsonSerializer.Serialize(comments);
    }
}
