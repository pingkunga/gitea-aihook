using System.Net;
using System.Text;
using GiteaAiSummarizer.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace GiteaAiSummarizer.Tests;

public class GiteaSkillTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"result\":\"ok\"}", Encoding.UTF8, "application/json")
                }
            );
        }
    }

    private static (GiteaSkill Skill, RecordingHandler Handler) CreateSkill()
    {
        var handler = new RecordingHandler();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Gitea:Url"] = "http://gitea.test",
                    ["Gitea:AccessToken"] = "token"
                }
            )
            .Build();
        var client = new GiteaApiClient(
            new HttpClient(handler),
            config,
            NullLogger<GiteaApiClient>.Instance
        );

        return (new GiteaSkill(client, NullLogger<GiteaSkill>.Instance), handler);
    }

    [Fact]
    public async Task GetIssueAsync_UsesGiteaIssueEndpoint()
    {
        var (skill, handler) = CreateSkill();

        var result = await skill.GetIssueAsync("acme", "widgets", 42);

        Assert.Equal("{\"result\":\"ok\"}", result);
        Assert.Equal("/api/v1/repos/acme/widgets/issues/42", Assert.Single(handler.Paths));
    }

    [Fact]
    public async Task SearchCodeAsync_UsesEncodedGiteaSearchEndpoint()
    {
        var (skill, handler) = CreateSkill();

        var result = await skill.SearchCodeAsync("acme", "widgets", "Get Widget");

        Assert.Equal("{\"result\":\"ok\"}", result);
        Assert.Equal(
            "/api/v1/repos/acme/widgets/search?q=Get%20Widget&limit=10",
            Assert.Single(handler.Paths)
        );
    }
}