using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GiteaAiSummarizer.Models;

namespace GiteaAiSummarizer.Services;

public class GiteaApiClient(HttpClient http, IConfiguration config, ILogger<GiteaApiClient> logger)
{
    private readonly string _baseUrl = config["Gitea:Url"]?.TrimEnd('/') ?? string.Empty;
    private readonly string _webHookToken = config["Gitea:WebhookToken"] ?? string.Empty;
    private readonly string _accessToken = config["Gitea:AccessToken"] ?? string.Empty;
    private readonly string _cfId = config["Gitea:CfClientId"] ?? string.Empty;
    private readonly string _cfSecret = config["Gitea:CfClientSecret"] ?? string.Empty;

    private void AddHeaders(HttpRequestMessage request)
    {
        // Gitea standard auth
        request.Headers.Authorization = new AuthenticationHeaderValue("token", _accessToken);
        
        // Ensure we request JSON content
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Required headers for Cloudflare Access (Service Token)
        if (!string.IsNullOrEmpty(_cfId))
        {
            request.Headers.Remove("CF-Access-Client-Id");
            request.Headers.Add("CF-Access-Client-Id", _cfId);
            logger.LogDebug("Added CF-Access-Client-Id header");
        }
        if (!string.IsNullOrEmpty(_cfSecret))
        {
            request.Headers.Remove("CF-Access-Client-Secret");
            request.Headers.Add("CF-Access-Client-Secret", _cfSecret);
            logger.LogDebug("Added CF-Access-Client-Secret header");
        }
    }

    public async Task<string> GetDiffAsync(
        string owner,
        string repo,
        int prNumber,
        CancellationToken ct = default
    )
    {
        // For Gitea, the public .diff URL is usually /{owner}/{repo}/pulls/{number}.diff
        var url = $"{_baseUrl}/api/v1/repos/{owner}/{repo}/pulls/{prNumber}.diff";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddHeaders(request);

        logger.LogInformation(
            "Fetching diff for {Owner}/{Repo}#{PR} via {Url}",
            owner,
            repo,
            prNumber,
            url
        );
        var response = await http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            logger.LogError(
                "Failed to fetch diff. Status: {Status}, Content: {Content}",
                response.StatusCode,
                error
            );
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<PullRequest> GetPullRequestAsync(
        string owner,
        string repo,
        int prNumber,
        CancellationToken ct = default
    )
    {
        var url = $"{_baseUrl}/api/v1/repos/{owner}/{repo}/pulls/{prNumber}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddHeaders(request);

        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            logger.LogError(
                "Failed to fetch pull request. Status: {Status}, Content: {Content}",
                response.StatusCode,
                error
            );
        }

        response.EnsureSuccessStatusCode();
        var pr = await response.Content.ReadFromJsonAsync<PullRequest>(cancellationToken: ct);
        return pr ?? throw new InvalidOperationException("Invalid pull request response from Gitea.");
    }

    public async Task CreateCommitStatusAsync(
        string owner,
        string repo,
        string sha,
        string context,
        string state,
        string description,
        string? targetUrl = null,
        CancellationToken ct = default
    )
    {
        var url = $"{_baseUrl}/api/v1/repos/{owner}/{repo}/statuses/{sha}";
        var payload = new GiteaCommitStatusRequest
        {
            Context = context,
            State = state,
            Description = description,
            TargetUrl = targetUrl
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        AddHeaders(request);
        request.Content = JsonContent.Create(payload);

        logger.LogInformation(
            "Setting commit status {Context}={State} for {Owner}/{Repo}@{Sha}",
            context,
            state,
            owner,
            repo,
            sha
        );

        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            logger.LogError(
                "Failed to set commit status. Status: {Status}, Content: {Content}",
                response.StatusCode,
                error
            );
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task PostCommentAsync(
        string owner,
        string repo,
        int prNumber,
        string body,
        CancellationToken ct = default
    )
    {
        var url = $"{_baseUrl}/api/v1/repos/{owner}/{repo}/issues/{prNumber}/comments";
        var payload = new GiteaCommentRequest { Body = body };
        var json = JsonSerializer.Serialize(payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        AddHeaders(request);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        logger.LogInformation("Posting comment to {Owner}/{Repo}#{PR}", owner, repo, prNumber);
        var response = await http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            logger.LogError(
                "Failed to post comment. Status: {Status}, Content: {Content}",
                response.StatusCode,
                error
            );
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task<List<GiteaIssueComment>> GetIssueCommentsAsync(
        string owner,
        string repo,
        int issueNumber,
        CancellationToken ct = default
    )
    {
        var url = $"{_baseUrl}/api/v1/repos/{owner}/{repo}/issues/{issueNumber}/comments";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddHeaders(request);

        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            logger.LogError(
                "Failed to fetch issue comments. Status: {Status}, Content: {Content}",
                response.StatusCode,
                error
            );
        }

        response.EnsureSuccessStatusCode();

        try
        {
            var comments = await response.Content.ReadFromJsonAsync<List<GiteaIssueComment>>(
                cancellationToken: ct
            );
            return comments ?? new List<GiteaIssueComment>();
        }
        catch (JsonException ex)
        {
            var rawContent = await response.Content.ReadAsStringAsync(ct);
            logger.LogError(
                ex,
                "Failed to parse JSON from Gitea comments API. Content starts with: {Preview}",
                rawContent.Length > 500 ? rawContent[..500] : rawContent
            );
            throw;
        }
    }

    public async Task UpdateIssueCommentAsync(
        string owner,
        string repo,
        long commentId,
        string body,
        CancellationToken ct = default
    )
    {
        var url = $"{_baseUrl}/api/v1/repos/{owner}/{repo}/issues/comments/{commentId}";
        var payload = new GiteaCommentRequest { Body = body };
        var json = JsonSerializer.Serialize(payload);

        using var request = new HttpRequestMessage(HttpMethod.Patch, url);
        AddHeaders(request);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            logger.LogError(
                "Failed to update comment. Status: {Status}, Content: {Content}",
                response.StatusCode,
                error
            );
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task<bool> CheckConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var url = $"{_baseUrl}/api/v1/version";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddHeaders(request);
            var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Connection check failed for Gitea at {Url}", _baseUrl);
            return false;
        }
    }
}
