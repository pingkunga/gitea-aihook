using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using GiteaAiSummarizer.Services;
using Microsoft.Agents.AI;

namespace GiteaAiSummarizer.Tests;

// Regression coverage for the "impact_graph.py hangs forever" bug: SkillScriptRunner used to close
// the subprocess's stdin only when the caller supplied `arguments`. A script that reads from stdin
// (like impact_graph.py) would then block on EOF forever, and since ProcessAsync is always invoked
// with CancellationToken.None from both webhook endpoints, nothing ever unblocked it.
//
// AgentFileSkill and AgentFileSkillScript both have internal-only constructors in
// Microsoft.Agents.AI (confirmed via reflection — the SDK's XML docs list them without making the
// accessibility clear), so this test builds them via reflection rather than a public API.
public class SkillScriptRunnerTests
{
    private static object CreateViaInternalCtor(Type type, params object?[] args)
    {
        var ctor = type
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(c => c.GetParameters().Length == args.Length);
        return ctor.Invoke(args);
    }

    private static (AgentFileSkill Skill, AgentFileSkillScript Script) CreateImpactGraphScript()
    {
        var scriptPath = Path.Combine(
            AppContext.BaseDirectory,
            "Templates",
            "Skills",
            "review",
            "scripts",
            "impact_graph.py"
        );
        Assert.True(File.Exists(scriptPath), $"Expected script at {scriptPath}");

        var frontmatter = new AgentSkillFrontmatter("review", "test skill", null);
        var skill = (AgentFileSkill)
            CreateViaInternalCtor(
                typeof(AgentFileSkill),
                frontmatter,
                "test",
                Path.GetDirectoryName(scriptPath)!,
                Array.Empty<AgentSkillResource>(),
                Array.Empty<AgentSkillScript>()
            );

        AgentFileSkillScriptRunner runner = SkillScriptRunner.RunAsync;
        var script = (AgentFileSkillScript)
            CreateViaInternalCtor(typeof(AgentFileSkillScript), "impact_graph", scriptPath, runner);

        return (skill, script);
    }

    [Fact]
    public async Task RunAsync_NoArguments_DoesNotHang()
    {
        var (skill, script) = CreateImpactGraphScript();
        var sw = Stopwatch.StartNew();

        // arguments: null — this is exactly the shape that used to hang forever, because stdin was
        // only closed inside `if (arguments.HasValue)`.
        var result = await SkillScriptRunner.RunAsync(
            skill,
            script,
            arguments: null,
            serviceProvider: null,
            CancellationToken.None
        );

        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"Expected a fast return, took {sw.Elapsed}");
        Assert.Contains("No significant symbols", result?.ToString());
    }

    [Fact]
    public async Task RunAsync_DiffSplitAcrossMultipleArrayElements_AnalyzesAllOfIt()
    {
        var (skill, script) = CreateImpactGraphScript();

        // The advertised schema is a bare array of strings with no field name, so a model could
        // plausibly split the diff into one element per line instead of one element for the whole
        // thing. read_diff() must join every element rather than read only the first.
        var lines = new[]
        {
            "diff --git a/foo.cs b/foo.cs",
            "+++ b/foo.cs",
            "+public class FooController {",
            "+    [HttpGet(\"/api/foo\")]",
            "+    public string GetFoo() { return \"x\"; }",
            "+}"
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(lines));

        var result = await SkillScriptRunner.RunAsync(
            skill,
            script,
            arguments: doc.RootElement.Clone(),
            serviceProvider: null,
            CancellationToken.None
        );

        var text = result?.ToString() ?? "";
        Assert.Contains("GetFoo", text);
        Assert.Contains("/api/foo", text);
    }
}
