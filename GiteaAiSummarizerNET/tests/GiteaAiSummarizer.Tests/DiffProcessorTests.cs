using GiteaAiSummarizer.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace GiteaAiSummarizer.Tests;

public class DiffProcessorTests
{
    private static DiffProcessor CreateProcessor(int maxFullDiffKb = 50)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["MaxDiffSizeKb"] = maxFullDiffKb.ToString() }
            )
            .Build();
        return new DiffProcessor(config, NullLogger<DiffProcessor>.Instance);
    }

    [Fact]
    public void DetermineStrategy_SmallDiff_ReturnsFullDiff()
    {
        var processor = CreateProcessor(maxFullDiffKb: 50);
        Assert.Equal(DiffStrategy.FullDiff, processor.DetermineStrategy("tiny diff"));
    }

    [Fact]
    public void DetermineStrategy_MediumDiff_ReturnsChunked()
    {
        var processor = CreateProcessor(maxFullDiffKb: 1);
        var diff = new string('x', 5 * 1024);
        Assert.Equal(DiffStrategy.Chunked, processor.DetermineStrategy(diff));
    }

    [Fact]
    public void DetermineStrategy_HugeDiff_ReturnsFileLevelSummary()
    {
        var processor = CreateProcessor(maxFullDiffKb: 1);
        var diff = new string('x', 250 * 1024);
        Assert.Equal(DiffStrategy.FileLevelSummary, processor.DetermineStrategy(diff));
    }

    [Fact]
    public void SplitByFile_SplitsOnGitDiffHeaders()
    {
        var processor = CreateProcessor();
        var diff = "diff --git a/foo.cs b/foo.cs\n+line1\ndiff --git a/bar.cs b/bar.cs\n+line2\n";

        var chunks = processor.SplitByFile(diff);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("foo.cs", chunks[0].FileName);
        Assert.Equal("bar.cs", chunks[1].FileName);
    }

    [Fact]
    public void BuildFileLevelSummary_CountsAddedAndRemovedLines()
    {
        var processor = CreateProcessor();
        var diff = "diff --git a/foo.cs b/foo.cs\n+++ b/foo.cs\n--- a/foo.cs\n+added\n+added2\n-removed\n";

        var summary = processor.BuildFileLevelSummary(diff);

        Assert.Contains("Total files changed: 1", summary);
        Assert.Contains("`foo.cs`", summary);
        Assert.Contains("+2", summary);
        Assert.Contains("-1", summary);
    }
}
