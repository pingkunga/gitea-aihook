using System.Net;
using System.Text;
using GiteaAiSummarizer.Models;
using GiteaAiSummarizer.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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

        // When set, the first call asks for this tool instead of replying with text.
        public string? CallToolFirst { get; init; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            var list = messages.ToList();
            Calls.Add(list);
            if (CallToolFirst is not null && Calls.Count == 1)
            {
                var call = new FunctionCallContent("call-1", CallToolFirst, new Dictionary<string, object?> { ["x"] = 1 });
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            }
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
    internal sealed class FakeGiteaHandler : HttpMessageHandler
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
        bool returnNoText = false,
        string? callToolFirst = null,
        IList<AITool>? tools = null,
        bool includeSkillOutputs = false,
        ILogger<SummaryService>? logger = null
    )
    {
        var chat = new FakeChatClient { ReturnNoText = returnNoText, CallToolFirst = callToolFirst };
        var agent = new ChatClientAgent(chat, name: "TestAgent", instructions: "test instructions", tools: tools);

        var handler = new FakeGiteaHandler { Diff = diff };
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://gitea.test") };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Gitea:Url"] = "http://gitea.test",
                    ["Gitea:AccessToken"] = "token",
                    ["AI:ENGINE_TYPE"] = "Ollama",
                    ["MaxDiffSizeKb"] = maxDiffSizeKb.ToString(),
                    ["Summary:IncludeSkillOutputs"] = includeSkillOutputs.ToString()
                }
            )
            .Build();

        var gitea = new GiteaApiClient(httpClient, config, NullLogger<GiteaApiClient>.Instance);
        var diffProcessor = new DiffProcessor(config, NullLogger<DiffProcessor>.Instance);
        var service = new SummaryService(gitea, agent, diffProcessor, config, logger ?? NullLogger<SummaryService>.Instance);

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
    public async Task ProcessAsync_SmallDiff_PromptCarriesOwnerRepoAndPrNumber()
    {
        var (service, chat, _) = CreateService(maxDiffSizeKb: 1000, diff: "diff --git a/foo.cs b/foo.cs\n+hi\n");

        await service.ProcessAsync(MakePayload());

        // gitea-tools scripts need owner/repo; without them in the prompt the model has to guess.
        var prompt = chat.Calls[0].Last().Text;
        Assert.Contains("owner: acme", prompt);
        Assert.Contains("repo: widgets", prompt);
        Assert.Contains("PR Number: 1", prompt);
    }

    [Fact]
    public async Task ProcessAsync_ChunkedDiff_RunsEachChunkInAFreshSessionThenConsolidates()
    {
        var diff =
            "diff --git a/foo.cs b/foo.cs\n+foo change\n"
            + "diff --git a/bar.cs b/bar.cs\n+bar change\n";
        // maxDiffSizeKb = 0 forces the Chunked strategy regardless of diff size.
        var (service, chat, handler) = CreateService(maxDiffSizeKb: 0, diff: diff);

        await service.ProcessAsync(MakePayload());

        // 2 file chunks + 1 consolidation call
        Assert.Equal(3, chat.Calls.Count);

        // Every turn runs in its own session, so none carries an earlier chunk's diff or tool traffic.
        Assert.Equal(chat.Calls[0].Count, chat.Calls[1].Count);
        Assert.Equal(chat.Calls[0].Count, chat.Calls[2].Count);
        Assert.DoesNotContain(chat.Calls[1], m => m.Text.Contains("foo change"));

        // With no shared history, each chunk call repeats the PR metadata.
        Assert.Contains("PR Title:", chat.Calls[0].Last().Text);
        Assert.Contains("PR Title:", chat.Calls[1].Last().Text);
        Assert.Contains("owner: acme", chat.Calls[1].Last().Text);

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessAsync_ToolCalled_CapturesItsResultIntoTheCommentOnlyWhenEnabled(bool include)
    {
        var tool = AIFunctionFactory.Create((int x) => $"demo-tool-output-{x}", "demo_tool");
        var (service, _, handler) = CreateService(
            maxDiffSizeKb: 1000,
            diff: "diff --git a/foo.cs b/foo.cs\n+hi\n",
            callToolFirst: "demo_tool",
            tools: [tool],
            includeSkillOutputs: include
        );

        await service.ProcessAsync(MakePayload());

        var posted = handler.Requests.Single(r => r.Method == "POST" && r.Path.EndsWith("/comments"));
        var comment = System.Text.Json.JsonDocument.Parse(posted.Body!).RootElement.GetProperty("body").GetString()!;
        if (include)
        {
            Assert.Contains("<details><summary>🛠 Skill outputs</summary>", comment);
            Assert.Contains("demo_tool", comment);
            Assert.Contains("demo-tool-output-1", comment);
        }
        else
        {
            Assert.DoesNotContain("Skill outputs", comment);
        }
    }

    // Captures formatted log lines so tests can assert on what an operator would see.
    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task ProcessAsync_ModelCallsNoTools_WarnsThatNoSkillRan()
    {
        var logger = new ListLogger<SummaryService>();
        var (service, _, _) = CreateService(maxDiffSizeKb: 1000, diff: "diff --git a/foo.cs b/foo.cs\n+hi\n", logger: logger);

        await service.ProcessAsync(MakePayload());

        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("Agent run at full-diff summary: 0 tool calls")
        );
    }

    [Fact]
    public async Task ProcessAsync_ModelCallsATool_LogsTheToolCountAtInformation()
    {
        var logger = new ListLogger<SummaryService>();
        var tool = AIFunctionFactory.Create((int x) => "ok", "demo_tool");
        var (service, _, _) = CreateService(
            maxDiffSizeKb: 1000,
            diff: "diff --git a/foo.cs b/foo.cs\n+hi\n",
            callToolFirst: "demo_tool",
            tools: [tool],
            logger: logger
        );

        await service.ProcessAsync(MakePayload());

        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("1 tool calls [demo_tool]")
        );
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
