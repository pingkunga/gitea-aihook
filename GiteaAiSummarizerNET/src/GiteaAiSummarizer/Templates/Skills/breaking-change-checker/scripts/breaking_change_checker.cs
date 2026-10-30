using System.Text.Json;
using System.Text.RegularExpressions;

var rawInput = Console.In.ReadToEnd();
var diff = ExtractDiff(rawInput);
var findings = new List<string>();

if (LooksLikeRouteChange(diff))
    findings.Add("[Critical] Route contract appears to change or disappear.");

if (LooksLikeAuthChange(diff))
    findings.Add("[Critical] Authentication or authorization contract appears to change.");

if (LooksLikeConfigChange(diff))
    findings.Add("[Warning] Environment/config names or required values appear to change.");

if (LooksLikeDockerContractChange(diff))
    findings.Add("[Warning] Docker or deployment runtime contract appears to change.");

if (LooksLikePublicApiChange(diff))
    findings.Add("[Warning] Public API or method signatures appear to change.");

Console.WriteLine("## Breaking Change Check");

if (findings.Count == 0)
{
    Console.WriteLine("Verdict: [OK] — No obvious breaking change detected.");
    return;
}

var critical = findings.Any(f => f.StartsWith("[Critical]"));
Console.WriteLine(critical
    ? "Verdict: [Critical] — likely breaking change detected."
    : "Verdict: [Warning] — migration or coordination may be required.");

foreach (var item in findings)
    Console.WriteLine($"- {item}");

static string ExtractDiff(string rawInput)
{
    if (string.IsNullOrWhiteSpace(rawInput))
        return string.Empty;

    try
    {
        var json = JsonDocument.Parse(rawInput);

        if (json.RootElement.ValueKind == JsonValueKind.Array)
        {
            var values = new List<string>();
            foreach (var element in json.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                    values.Add(element.GetString() ?? string.Empty);
            }

            if (values.Count > 0)
                return string.Join(Environment.NewLine, values);
        }

        if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("diff", out var diffProp))
            return diffProp.GetString() ?? rawInput;
    }
    catch
    {
        // Fall back to the raw input when the content is not JSON.
    }

    return rawInput;
}

static bool LooksLikeRouteChange(string diff)
{
    var patterns = new[]
    {
        "Map(Get|Post|Put|Delete|Patch)\\s*\\(\\s*[\"']([^\"']+)[\"']",
        "Http(Get|Post|Put|Delete|Patch)\\s*\\(\\s*[\"']([^\"']+)[\"']",
        "@(?:Get|Post|Put|Delete|Patch|Request)Mapping\\s*\\(\\s*(?:value\\s*=\\s*)?[\"']([^\"']+)[\"']",
        "Route\\s*\\(\\s*[\"']([^\"']+)[\"']"
    };

    return patterns.Any(p => Regex.IsMatch(diff, p, RegexOptions.Multiline | RegexOptions.IgnoreCase));
}

static bool LooksLikeAuthChange(string diff)
{
    var patterns = new[]
    {
        "Authorization",
        "Bearer",
        "X-Api-Key",
        "WebhookSecret",
        "UseAuthentication",
        "AddAuthentication",
        "RequireAuth",
        "AuthHeader"
    };

    return patterns.Any(p => Regex.IsMatch(diff, Regex.Escape(p), RegexOptions.IgnoreCase));
}

static bool LooksLikeConfigChange(string diff)
{
    var patterns = new[]
    {
        @"Gitea__\w+",
        @"AI__\w+",
        @"Environment\.GetEnvironmentVariable",
        @"appsettings",
        @"\.env"
    };

    return patterns.Any(p => Regex.IsMatch(diff, p, RegexOptions.IgnoreCase));
}

static bool LooksLikeDockerContractChange(string diff)
{
    var patterns = new[]
    {
        @"^\+EXPOSE\s+",
        @"^\+ports:\s*",
        @"^\+FROM\s+",
        @"docker-compose",
        @"image:\s*",
        @"ports:\s*"
    };

    return patterns.Any(p => Regex.IsMatch(diff, p, RegexOptions.Multiline | RegexOptions.IgnoreCase));
}

static bool LooksLikePublicApiChange(string diff)
{
    var patterns = new[]
    {
        @"^\+\s*(?:public|internal|protected)\s+[\w<>,\[\]\?\s\.]+\s+\w+\s*\(",
        @"^-\s*(?:public|internal|protected)\s+[\w<>,\[\]\?\s\.]+\s+\w+\s*\("
    };

    return patterns.Any(p => Regex.IsMatch(diff, p, RegexOptions.Multiline));
}
