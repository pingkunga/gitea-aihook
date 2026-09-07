using System.ComponentModel;
using System.Text.Json;
using Microsoft.Agents.AI;

namespace GiteaAiSummarizer.Services;

public sealed class GiteaSkill(GiteaApiClient gitea, ILogger<GiteaSkill> logger)
    : AgentClassSkill<GiteaSkill>
{
    public override AgentSkillFrontmatter Frontmatter { get; } =
        new(
            "gitea-tools",
            "Provides access to Gitea repository information including issues, comments, and code search."
        );

    protected override string Instructions =>
        """
        Use this skill to gather additional context from the Gitea repository.

        1. Read `gitea-tool-catalog` before selecting a Gitea tool.
        2. Follow its selection rules and limitations.
        3. Only report conclusions supported by the diff or tool results.
        """;

    [AgentSkillScript("get_issue")]
    [Description("Fetches detailed information about a Gitea issue or pull request by its number.")]
    public async Task<string> GetIssueAsync(
        [Description("The owner of the repository.")] string owner,
        [Description("The name of the repository.")] string repo,
        [Description("The issue or pull request number.")] int number,
        CancellationToken ct = default
    )
    {
        logger.LogInformation("Skill Call: Fetching issue details for {Owner}/{Repo}#{Number}", owner, repo, number);
        return await gitea.GetIssueAsync(owner, repo, number, ct);
    }

    [AgentSkillScript("get_issue_comments")]
    [Description("Fetches comments for a specific Gitea issue or pull request.")]
    public async Task<string> GetIssueCommentsAsync(
        [Description("The owner of the repository.")] string owner,
        [Description("The name of the repository.")] string repo,
        [Description("The issue or pull request number.")] int number,
        CancellationToken ct = default
    )
    {
        logger.LogInformation("Skill Call: Fetching comments for {Owner}/{Repo}#{Number}", owner, repo, number);
        var comments = await gitea.GetIssueCommentsAsync(owner, repo, number, ct);
        return JsonSerializer.Serialize(comments);
    }

    [AgentSkillScript("search_code")]
    [Description("Searches for a keyword or symbol within the repository code.")]
    public async Task<string> SearchCodeAsync(
        [Description("The owner of the repository.")] string owner,
        [Description("The name of the repository.")] string repo,
        [Description("The keyword or symbol to search for.")] string keyword,
        CancellationToken ct = default
    )
    {
        logger.LogInformation("Skill Call: Searching code for '{Keyword}' in {Owner}/{Repo}", keyword, owner, repo);
        return await gitea.SearchCodeAsync(owner, repo, keyword, ct);
    }

    [AgentSkillResource("gitea-tool-catalog")]
    [Description("Lookup table for selecting the appropriate Gitea tool during pull request analysis.")]
    public string GiteaToolCatalog =>
        """
        # Gitea Tool Catalog

        | Need | Tool | Required inputs | Limitation |
        |---|---|---|---|
        | Read an issue or PR's metadata | `get_issue` | owner, repo, number | Returns raw Gitea JSON |
        | Understand issue or PR discussion | `get_issue_comments` | owner, repo, number | Returns raw JSON comments |
        | Find references to a changed symbol | `search_code` | owner, repo, keyword | Maximum 10 matches; no match does not prove unused |

        ## Selection rules
        - PR text or diff refers to `#<number>`: use `get_issue`.
        - Acceptance criteria or prior decisions may be in a discussion: use `get_issue_comments`.
        - `impact_graph` identifies a changed symbol: use `search_code` once per unique symbol.
        - Treat empty or incomplete search results as inconclusive.
        """;
}