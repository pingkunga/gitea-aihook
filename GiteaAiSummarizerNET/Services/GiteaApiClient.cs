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
        //request.Headers.Authorization = new AuthenticationHeaderValue("token", _webHookToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("token", _accessToken);
        // Required headers for Cloudflare Access (Service Token)
        if (!string.IsNullOrEmpty(_cfId))
            request.Headers.Add("CF-Access-Client-Id", _cfId);
        if (!string.IsNullOrEmpty(_cfSecret))
            request.Headers.Add("CF-Access-Client-Secret", _cfSecret);
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
