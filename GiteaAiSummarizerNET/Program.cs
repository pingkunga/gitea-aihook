using System.Text.Json;
using GiteaAiSummarizer.Models;
using GiteaAiSummarizer.Services;
using Serilog;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI.Hosting;

// ── Serilog bootstrap ────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog(
    (ctx, services, cfg) =>
        cfg.ReadFrom
            .Configuration(ctx.Configuration)
            .ReadFrom.Services(services)
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}"
            )
);

// ── AI Agent Setup ───────────────────────────────────────────────────────────
// Register IChatClient as singleton since it's used by singleton services
builder.Services.AddSingleton<IChatClient>(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    var aiConfig = config.GetSection("AI");
    return ChatClientFactory.CreateChatClient(
        aiConfig["ENGINE_TYPE"],
        aiConfig["ENDPOINT"],
        aiConfig["MODEL_NAME"],
        aiConfig["API_KEY"]
    );
});
builder.AddAIAgent(
    "GiteaSummarizerAgent",
    (sp, key) =>
    {
        var chatClient = sp.GetRequiredService<IChatClient>();

        return new ChatClientAgent(
            chatClient,
            name: key,
            instructions: """
            You are a helpful assistant for summarizing pull request diffs in Gitea. Given a diff, you will produce a concise summary of the changes, including what was changed and why if possible. Focus on the intent and impact of the changes rather than just listing them. Use the following format for your summary:
            """
        // tools: [
        //     .. tools.Cast<AITool>()
        // ]
        );
    }
);

// .Build(sp =>
// {
//     var config = sp.GetRequiredService<IConfiguration>();
//     var aiConfig = config.GetSection("AI");

//     return ChatClientFactory.CreateChatClient(
//         aiConfig["ENGINE_TYPE"],
//         aiConfig["ENDPOINT"],
//         aiConfig["MODEL_NAME"],
//         aiConfig["API_KEY"]
//     );
// });

// ── Services ─────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<WebhookVerifier>();
builder.Services.AddHttpClient<GiteaApiClient>(
    (sp, client) =>
    {
        var config = sp.GetRequiredService<IConfiguration>();

        // ตั้งค่าพื้นฐาน
        client.Timeout = TimeSpan.FromMinutes(5);

        // --- เพิ่ม Cloudflare Access Header ---
        var cfId = config["Gitea:CfClientId"];
        var cfSecret = config["Gitea:CfClientSecret"];

        if (!string.IsNullOrEmpty(cfId))
            client.DefaultRequestHeaders.Add("CF-Access-Client-Id", cfId);
        if (!string.IsNullOrEmpty(cfSecret))
            client.DefaultRequestHeaders.Add("CF-Access-Client-Secret", cfSecret);
    }
);
builder.Services.AddSingleton<DiffProcessor>();
builder.Services.AddScoped<SummaryService>();

// Allow up to 100MB bodies and disable minimum data rate limits to prevent premature timeouts
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 104857600; // 100MB
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(1);
    options.Limits.MinRequestBodyDataRate = null;
    options.Limits.MinResponseDataRate = null;
});

var app = builder.Build();

app.UseSerilogRequestLogging();

var startedAt = DateTimeOffset.UtcNow;

// ── Webhook endpoint ─────────────────────────────────────────────────────────
app.MapPost(
    "/api/v1/webhook/gitea",
    async (
        HttpContext ctx,
        WebhookVerifier verifier,
        SummaryService summary,
        ILogger<Program> logger
    ) =>
    {
        ctx.Request.EnableBuffering();

        // Log basic info
        var contentLength = ctx.Request.ContentLength;
        logger.LogInformation("Incoming Webhook: Content-Length={Length}", contentLength);

        string body;
        try
        {
            // Set a very generous timeout for the initial read from network
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
            body = await reader.ReadToEndAsync(cts.Token);
            ctx.Request.Body.Position = 0;
            logger.LogInformation("Successfully read body: {ActualLength} bytes", body.Length);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Kestrel/Network failed to stream body. This usually means the sender (Gitea/Proxy) closed the connection prematurely."
            );
            return Results.StatusCode(408); // Request Timeout
        }

        var signature = ctx.Request.Headers["X-Gitea-Signature"].FirstOrDefault();
        if (!verifier.Verify(body, signature))
        {
            logger.LogWarning("Webhook signature verification failed");
            return Results.Unauthorized();
        }

        // Parse quickly
        var payload = JsonSerializer.Deserialize<GiteaPayload>(body);
        if (payload is null)
            return Results.BadRequest();

        if (payload.Action is not ("opened" or "synchronized" or "reopened" or "edited"))
        {
            return Results.Ok(new { status = "ignored" });
        }

        // --- RESPOND IMMEDIATELY ---
        // Gitea will see "202 Accepted" and close the connection successfully
        _ = Task.Run(async () =>
        {
            try
            {
                logger.LogInformation("Processing PR #{Number} in background...", payload.Number);
                await summary.ProcessAsync(payload);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background error for PR #{Number}", payload.Number);
            }
        });

        return Results.Accepted();
    }
);

// ── Manual trigger endpoint ───────────────────────────────────────────────────
app.MapPost(
    "/api/v1/summarize/{owner}/{repo}/{prNumber:int}",
    async (
        string owner,
        string repo,
        int prNumber,
        HttpContext ctx,
        GiteaApiClient gitea,
        SummaryService summary,
        IConfiguration config,
        ILogger<Program> logger
    ) =>
    {
        // Admin token is required — block entirely if not configured
        var adminToken = config["AdminToken"];
        if (string.IsNullOrEmpty(adminToken))
            return Results.Unauthorized();

        var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault();
        if (
            authHeader is null
            || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        )
            return Results.Unauthorized();

        var token = authHeader["Bearer ".Length..].Trim();
        if (!string.Equals(token, adminToken, StringComparison.Ordinal))
            return Results.Unauthorized();

        PullRequest realPr;
        try
        {
            realPr = await gitea.GetPullRequestAsync(owner, repo, prNumber, ctx.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Manual trigger failed to resolve PR {Owner}/{Repo}#{PR}", owner, repo, prNumber);
            return Results.BadRequest(new { error = "cannot resolve pull request" });
        }

        var fakePayload = new GiteaPayload
        {
            Action = "opened",
            Number = prNumber,
            PullRequest = new()
            {
                Number = prNumber,
                Title = realPr.Title,
                Body = realPr.Body,
                HtmlUrl = realPr.HtmlUrl,
                Base = new() { Ref = realPr.Base.Ref },
                Head = new() { Ref = realPr.Head.Ref, Sha = realPr.Head.Sha }
            },
            Repository = new() { FullName = $"{owner}/{repo}" }
        };

        logger.LogInformation("Manual trigger: {Owner}/{Repo}#{PR}", owner, repo, prNumber);
        _ = Task.Run(() => summary.ProcessAsync(fakePayload));

        return Results.Accepted(null, new { status = "queued", pr = prNumber });
    }
);

// ── Health check ─────────────────────────────────────────────────────────────
app.MapGet(
    "/health",
    async (GiteaApiClient gitea) =>
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var giteaOk = await gitea.CheckConnectionAsync(cts.Token);
        //var aiOk = await ai.CheckAvailabilityAsync(cts.Token);
        var uptime = DateTimeOffset.UtcNow - startedAt;

        return Results.Ok(
            new
            {
                //status = giteaOk && aiOk ? "healthy" : "degraded",
                gitea = giteaOk ? "connected" : "unreachable",
                //ai = aiOk ? $"{ai.ProviderName}: available" : $"{ai.ProviderName}: unavailable",
                uptime = uptime.ToString(@"d\d\ hh\:mm\:ss").TrimStart('0', 'd', ' ')
            }
        );
    }
);

app.Run();
