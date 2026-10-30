using System.Diagnostics;

namespace GiteaAiSummarizer.Tests;

public class BreakingChangeAnalyzerTests
{
    [Fact]
    public async Task Script_WhenRouteAndAuthChanged_ReturnsCritical()
    {
        var diff = """
            + MapGet("/api/v1/login");
            + Authorization;
            + ports: 8080:8080
            """;

        var output = await RunSkillScriptAsync(diff);

        Assert.Contains("Verdict: [Critical]", output);
        Assert.Contains("Route contract appears to change or disappear.", output);
        Assert.Contains("Authentication or authorization contract appears to change.", output);
    }

    [Fact]
    public async Task Script_WhenConfigChanged_ReturnsWarning()
    {
        var diff = """
            - Gitea__AccessToken
            + Gitea__NewAccessToken
            """;

        var output = await RunSkillScriptAsync(diff);

        Assert.Contains("Verdict: [Warning]", output);
        Assert.Contains("Environment/config names or required values appear to change.", output);
    }

    [Fact]
    public async Task Script_WhenNoRiskyPatterns_ReturnsNoIssue()
    {
        var diff = """
            + Console.WriteLine("hello");
            + var value = 1;
            """;

        var output = await RunSkillScriptAsync(diff);

        Assert.Contains("Verdict: [OK]", output);
        Assert.Contains("No obvious breaking change detected.", output);
    }

    private static async Task<string> RunSkillScriptAsync(string diff)
    {
        var scriptDirectory = GetSkillScriptDirectory();

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = scriptDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.StartInfo.ArgumentList.Add("run");
        process.StartInfo.ArgumentList.Add("breaking_change_checker.cs");

        process.Start();
        await process.StandardInput.WriteAsync(diff);
        await process.StandardInput.FlushAsync();
        process.StandardInput.Close();

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new Xunit.Sdk.XunitException($"dotnet run failed for the breaking-change-checker skill.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        }

        return stdout;
    }

    private static string GetSkillScriptDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var solutionPath = Path.Combine(directory.FullName, "GiteaAiSummarizer.sln");
            if (File.Exists(solutionPath))
            {
                return Path.Combine(
                    directory.FullName,
                    "src",
                    "GiteaAiSummarizer",
                    "Templates",
                    "Skills",
                    "breaking-change-checker",
                    "scripts"
                );
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the GiteaAiSummarizer solution root from AppContext.BaseDirectory.");
    }
}
