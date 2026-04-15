using System.Text.Json.Serialization;

namespace GiteaAiSummarizer.Models;

public class GiteaPayload
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("pull_request")]
    public PullRequest PullRequest { get; set; } = new();

    [JsonPropertyName("repository")]
    public Repository Repository { get; set; } = new();

    [JsonPropertyName("sender")]
    public GiteaUser? Sender { get; set; }
}

public class PullRequest
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("diff_url")]
    public string DiffUrl { get; set; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("base")]
    public BranchRef Base { get; set; } = new();

    [JsonPropertyName("head")]
    public BranchRef Head { get; set; } = new();

    [JsonPropertyName("user")]
    public GiteaUser? User { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }
}

public class BranchRef
{
    [JsonPropertyName("ref")]
    public string Ref { get; set; } = string.Empty;

    [JsonPropertyName("sha")]
    public string Sha { get; set; } = string.Empty;

    [JsonPropertyName("repo")]
    public Repository? Repo { get; set; }
}

public class Repository
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("full_name")]
    public string FullName { get; set; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("owner")]
    public GiteaUser? Owner { get; set; }
}

public class GiteaUser
{
    [JsonPropertyName("login")]
    public string Login { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public long Id { get; set; }
}

public class GiteaCommentRequest
{
    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;
}