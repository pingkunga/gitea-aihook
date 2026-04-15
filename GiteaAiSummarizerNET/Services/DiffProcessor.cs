namespace GiteaAiSummarizer.Services;

public enum DiffStrategy
{
    FullDiff,
    Chunked,
    FileLevelSummary
}

public record DiffChunk(string FileName, string Content);

public class DiffProcessor(IConfiguration config, ILogger<DiffProcessor> logger)
{
    private readonly int _maxFullDiffKb = config.GetValue<int>("MaxDiffSizeKb", 50);
    private const int ChunkedMaxKb = 200;

    public DiffStrategy DetermineStrategy(string diff)
    {
        var sizeKb = System.Text.Encoding.UTF8.GetByteCount(diff) / 1024;
        var strategy = sizeKb switch
        {
            _ when sizeKb < _maxFullDiffKb => DiffStrategy.FullDiff,
            _ when sizeKb <= ChunkedMaxKb => DiffStrategy.Chunked,
            _ => DiffStrategy.FileLevelSummary
        };
        logger.LogInformation("Diff size: {SizeKb} KB → strategy: {Strategy}", sizeKb, strategy);
        return strategy;
    }

    /// <summary>Splits a unified diff into per-file chunks.</summary>
    public IReadOnlyList<DiffChunk> SplitByFile(string diff)
    {
        var chunks = new List<DiffChunk>();
        var currentFileName = string.Empty;
        var currentLines = new System.Text.StringBuilder();

        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(currentFileName))
                    chunks.Add(new DiffChunk(currentFileName, currentLines.ToString()));

                // Extract file name: diff --git a/foo.go b/foo.go  →  foo.go
                var parts = line.Split(' ');
                currentFileName = parts.Length >= 4 ? StripGitPrefix(parts[^1]) : line;
                currentLines.Clear();
            }
            currentLines.AppendLine(line);
        }

        if (!string.IsNullOrEmpty(currentFileName))
            chunks.Add(new DiffChunk(currentFileName, currentLines.ToString()));

        return chunks;
    }

    /// <summary>Produces a compact file-list summary for very large diffs.</summary>
    public string BuildFileLevelSummary(string diff)
    {
        var files = new List<(string Name, int Added, int Removed)>();

        string? currentFile = null;
        int added = 0,
            removed = 0;

        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                if (currentFile is not null)
                    files.Add((currentFile, added, removed));

                var parts = line.Split(' ');
                currentFile = parts.Length >= 4 ? StripGitPrefix(parts[^1]) : line;
                added = removed = 0;
            }
            else if (line.StartsWith('+') && !line.StartsWith("+++"))
                added++;
            else if (line.StartsWith('-') && !line.StartsWith("---"))
                removed++;
        }

        if (currentFile is not null)
            files.Add((currentFile, added, removed));

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Total files changed: {files.Count}");
        sb.AppendLine();
        sb.AppendLine("| File | +Lines | -Lines |");
        sb.AppendLine("|------|--------|--------|");
        foreach (var (name, a, r) in files)
            sb.AppendLine($"| `{name}` | +{a} | -{r} |");

        return sb.ToString();
    }

    // "b/path/to/file.go" → "path/to/file.go"
    private static string StripGitPrefix(string path) =>
        path.StartsWith("b/", StringComparison.Ordinal) ? path[2..] : path;
}
