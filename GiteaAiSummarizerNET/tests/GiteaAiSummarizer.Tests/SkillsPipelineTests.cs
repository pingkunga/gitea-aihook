using System.Text.Json;
using System.Text.RegularExpressions;
using GiteaAiSummarizer.Models;
using GiteaAiSummarizer.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace GiteaAiSummarizer.Tests;

// End-to-end over the real skills pipeline, wired the way Program.cs wires it: file skills from
// Templates/Skills, SkillScriptRunner, approvals disabled. Guards against three silent failures:
// the framework no longer discovering the skills, the Disable*Approval flags no longer taking effect
// (every script call would become an approval request and the PR would get no summary), and the
// diff not reaching the scripts through SkillRunContext when the model passes no arguments.
public class SkillsPipelineTests
{
    // Plays the model: load both skills, run each one's script with no arguments, then answer.
    private sealed class SkillCallingChatClient : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            CallCount++;
            var history = messages.ToList();

            if (CallCount == 1)
            {
                return Reply(
                    new FunctionCallContent("load-1", "load_skill", new Dictionary<string, object?> { ["skillName"] = "review" }),
                    new FunctionCallContent("load-2", "load_skill", new Dictionary<string, object?> { ["skillName"] = "breaking-change-checker" })
                );
            }

            if (CallCount == 2)
            {
                // Use the script names exactly as load_skill listed them, rather than guessing.
                var loaded = string.Join(
                    "\n",
                    history.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.Result?.ToString())
                );
                var python = Regex.Match(loaded, @"<script name=""([^""]*impact_graph\.py)""").Groups[1].Value;
                var csharp = Regex.Match(loaded, @"<script name=""([^""]*breaking_change_checker\.cs)""").Groups[1].Value;
                Assert.False(string.IsNullOrEmpty(python), $"impact_graph.py not listed by load_skill:\n{loaded}");
                Assert.False(string.IsNullOrEmpty(csharp), $"breaking_change_checker.cs not listed by load_skill:\n{loaded}");

                return Reply(
                    new FunctionCallContent("run-1", "run_skill_script", new Dictionary<string, object?> { ["skillName"] = "review", ["scriptName"] = python }),
                    new FunctionCallContent("run-2", "run_skill_script", new Dictionary<string, object?> { ["skillName"] = "breaking-change-checker", ["scriptName"] = csharp })
                );
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "final summary")));
        }

        private static Task<ChatResponse> Reply(params AIContent[] calls) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, calls)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    [Fact]
    public async Task ProcessAsync_ModelRunsFileScriptsWithNoArguments_ScriptsAnalyzeTheRealDiff()
    {
        var provider = new AgentSkillsProviderBuilder()
            .UseFileSkill(Path.Combine(AppContext.BaseDirectory, "Templates", "Skills"))
            .UseFileScriptRunner(SkillScriptRunner.RunAsync)
            .UseOptions(o =>
            {
                o.DisableLoadSkillApproval = true;
                o.DisableReadSkillResourceApproval = true;
                o.DisableRunSkillScriptApproval = true;
            })
            .UseLoggerFactory(NullLoggerFactory.Instance)
            .Build();

        var chat = new SkillCallingChatClient();
        var agent = new ChatClientAgent(
            chat,
            new ChatClientAgentOptions { Name = "TestAgent", AIContextProviders = [provider] }
        );

        // A symbol and a route that exist only in this diff, so script output can only come from it.
        var diff = """
            diff --git a/WidgetEndpoints.cs b/WidgetEndpoints.cs
            +++ b/WidgetEndpoints.cs
            +    public string GetUniqueWidgetZq() { return "x"; }
            +app.MapGet("/api/v1/widgets-zq", () => "ok");
            """;
        var handler = new SummaryServiceTests.FakeGiteaHandler { Diff = diff };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Gitea:Url"] = "http://gitea.test",
                    ["Gitea:AccessToken"] = "token",
                    ["AI:ENGINE_TYPE"] = "Ollama",
                    ["MaxDiffSizeKb"] = "1000",
                    ["Summary:IncludeSkillOutputs"] = "true"
                }
            )
            .Build();
        var gitea = new GiteaApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://gitea.test") },
            config,
            NullLogger<GiteaApiClient>.Instance
        );
        var service = new SummaryService(
            gitea,
            agent,
            new DiffProcessor(config, NullLogger<DiffProcessor>.Instance),
            config,
            NullLogger<SummaryService>.Instance
        );

        await service.ProcessAsync(
            new GiteaPayload
            {
                Action = "opened",
                Number = 7,
                Repository = new Repository { FullName = "acme/widgets" },
                PullRequest = new PullRequest
                {
                    Number = 7,
                    Title = "Widgets",
                    HtmlUrl = "http://gitea.test/acme/widgets/pulls/7",
                    Base = new BranchRef { Ref = "main" },
                    Head = new BranchRef { Ref = "feat/zq", Sha = "abc123" }
                }
            }
        );

        Assert.Equal(3, chat.CallCount);
        var posted = handler.Requests.Single(r => r.Method == "POST" && r.Path.EndsWith("/comments"));
        var comment = JsonDocument.Parse(posted.Body!).RootElement.GetProperty("body").GetString()!;

        Assert.Contains("final summary", comment);
        // Python script (review skill) got the diff from SkillRunContext, not from arguments.
        Assert.Contains("## Impact Candidates Detected", comment);
        Assert.Contains("GetUniqueWidgetZq", comment);
        // C# script (breaking-change-checker skill) likewise.
        Assert.Contains("## Breaking Change Check", comment);
        Assert.Contains("Route contract appears to change", comment);
    }
}
