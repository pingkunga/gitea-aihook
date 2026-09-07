using System.Net;
using System.Text;
using GiteaAiSummarizer.Models;
using GiteaAiSummarizer.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace GiteaAiSummarizer.Tests;

public class SummaryServiceTests
{
    // Fake IChatClient: records every message list it's called with so tests can
    // assert on session/history behavior, and replies with a canned per-call text.
    private sealed class FakeChatClient : IChatClient
    {
        public List<List<ChatMessage>> Calls { get; } = new();

        // Simulates a turn that ends on tool calls: messages come back, but no text content.
        public bool ReturnNoText { get; init; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            var list = messages.ToList();
            Calls.Add(list);
            var reply = ReturnNoText ? "" : $"response-{Calls.Count - 1}";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    // Fake Gitea backend: serves a diff, an empty comment list, and records every request.
    private sealed class FakeGiteaHandler : HttpMessageHandler
    {
        public string Diff { get; init; } = "diff --git a/foo.cs b/foo.cs\n+hi\n";
        public List<GiteaIssueComment> ExistingComments { get; init; } = new();
        public List<(string Method, string Path, string? Body)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method.Method, path, body));

            string json = path.EndsWith(".diff") ? Diff
                : path.EndsWith("/comments") && request.Method == HttpMethod.Get
                    ? System.Text.Json.JsonSerializer.Serialize(ExistingComments)
                : "{}";

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                }
            );
        }
    }

    private static (SummaryService Service, FakeChatClient Chat, FakeGiteaHandler Handler) CreateService(
        int maxDiffSizeKb,
        string diff,
        bool returnNoText = false
    )
    {
        var chat = new FakeChatClient { ReturnNoText = returnNoText };
        var agent = new ChatClientAgent(chat, name: "TestAgent", instructions: "test instructions");

        var handler = new FakeGiteaHandler { Diff = diff };
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://gitea.test") };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Gitea:Url"] = "http://gitea.test",
                    ["Gitea:AccessToken"] = "token",
                    ["AI:ENGINE_TYPE"] = "Ollama",
                    ["MaxDiffSizeKb"] = maxDiffSizeKb.ToString()
                }
            )
            .Build();

        var gitea = new GiteaApiClient(httpClient, config, NullLogger<GiteaApiClient>.Instance);
        var diffProcessor = new DiffProcessor(config, NullLogger<DiffProcessor>.Instance);
        var service = new SummaryService(gitea, agent, diffProcessor, config, NullLogger<SummaryService>.Instance);

        return (service, chat, handler);
    }

    private static GiteaPayload MakePayload(string owner = "acme", string repo = "widgets") =>
        new()
        {
            Action = "opened",
            Number = 1,
            Repository = new Repository { FullName = $"{owner}/{repo}" },
            PullRequest = new PullRequest
            {
                Number = 1,
                Title = "Add widget",
                Body = "Adds a widget",
                HtmlUrl = "http://gitea.test/acme/widgets/pulls/1",
                Base = new BranchRef { Ref = "main" },
                Head = new BranchRef { Ref = "feat/widget", Sha = "abc123" }
            }
        };

    [Fact]
    public async Task ProcessAsync_SmallDiff_CallsAgentOnceAndPostsComment()
    {
        var (service, chat, handler) = CreateService(maxDiffSizeKb: 1000, diff: "diff --git a/foo.cs b/foo.cs\n+hi\n");

        await service.ProcessAsync(MakePayload());

        Assert.Single(chat.Calls);
        var posted = handler.Requests.Single(r => r.Method == "POST" && r.Path.EndsWith("/comments"));
        Assert.Contains("response-0", posted.Body);
        Assert.Contains("powered by Ollama", posted.Body);
    }

    [Fact]
    public async Task ProcessAsync_ChunkedDiff_SharesSessionAcrossChunksThenConsolidatesInAFreshOne()
    {
        var diff =
            "diff --git a/foo.cs b/foo.cs\n+foo change\n"
            + "diff --git a/bar.cs b/bar.cs\n+bar change\n";
        // maxDiffSizeKb = 0 forces the Chunked strategy regardless of diff size.
        var (service, chat, handler) = CreateService(maxDiffSizeKb: 0, diff: diff);

        await service.ProcessAsync(MakePayload());

        // 2 file chunks + 1 consolidation call
        Assert.Equal(3, chat.Calls.Count);

        // The chunk turns share one session: each carries more history than the last.
        Assert.True(chat.Calls[1].Count > chat.Calls[0].Count);

        // The consolidation turn runs in a fresh session, so it carries only its own prompt
        // instead of the whole diff plus every tool result the chunk session accumulated.
        Assert.True(chat.Calls[2].Count < chat.Calls[1].Count);
        Assert.Equal(chat.Calls[0].Count, chat.Calls[2].Count);

        // Only the first chunk call should carry PR metadata; later chunk calls stay short.
        Assert.Contains("PR Title:", chat.Calls[0].Last().Text);
        Assert.DoesNotContain("PR Title:", chat.Calls[1].Last().Text);

        // The per-file summaries are passed explicitly, because a chunk turn that ends on tool
        // calls would leave nothing in the session history for the consolidation turn to find.
        var consolidationText = chat.Calls[2].Last().Text;
        Assert.Contains("Consolidate these file summaries", consolidationText);
        Assert.Contains("PR Title:", consolidationText);
        Assert.Contains("foo.cs", consolidationText);
        Assert.Contains("bar.cs", consolidationText);
        Assert.Contains("response-0", consolidationText);
        Assert.Contains("response-1", consolidationText);

        var posted = handler.Requests.Single(r => r.Method == "POST" && r.Path.EndsWith("/comments"));
        Assert.Contains("response-2", posted.Body); // the consolidation call's reply
    }

    [Fact]
    public async Task ProcessAsync_AgentReturnsNoText_PostsNoCommentAndFailsTheCommitStatus()
    {
        var (service, _, handler) = CreateService(
            maxDiffSizeKb: 1000,
            diff: "diff --git a/foo.cs b/foo.cs\n+hi\n",
            returnNoText: true
        );

        await service.ProcessAsync(MakePayload());

        // An empty summary is a failure, not a success with placeholder text.
        Assert.DoesNotContain(handler.Requests, r => r.Method == "POST" && r.Path.EndsWith("/comments"));

        var statuses = handler
            .Requests.Where(r => r.Method == "POST" && r.Path.Contains("/statuses/"))
            .ToList();
        Assert.Contains(statuses, r => r.Body!.Contains("failure"));
        Assert.DoesNotContain(statuses, r => r.Body!.Contains("success"));
    }

    [Fact]
    public async Task ProcessAsync_ExistingSummaryComment_UpdatesInsteadOfPosting()
    {
        var (service, _, handler) = CreateService(maxDiffSizeKb: 1000, diff: "diff --git a/foo.cs b/foo.cs\n+hi\n");
        handler.ExistingComments.Add(
            new GiteaIssueComment { Id = 42, Body = "<!-- gitea-ai-summarizer:pr-summary -->\nold summary" }
        );

        await service.ProcessAsync(MakePayload());

        Assert.DoesNotContain(handler.Requests, r => r.Method == "POST" && r.Path.EndsWith("/comments"));
        Assert.Contains(handler.Requests, r => r.Method == "PATCH" && r.Path.EndsWith("/comments/42"));
    }
}
