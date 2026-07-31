using System.Text;
using System.Text.Json;
using Stryker.Abstractions;

namespace Stryker.TestRunner.MicrosoftTestPlatform;

/// <summary>
/// Persists opt-in mutation-campaign classification evidence. The plan detail
/// names every planned mutant with its isolation classification and
/// assessing-test width; the coverage trace preserves each class-boundary
/// per-test coverage map before the runner deletes it. Both exist because the
/// aggregate progress snapshot counts isolation-required mutants without
/// explaining which mutants were classified that way or which coverage records
/// widened their assessing sets.
/// </summary>
internal static class MutationCampaignDiagnostics
{
    internal const string PlanDetailFileEnvironmentVariable =
        "THREADWAY_STRYKER_PLAN_DETAIL_FILE";
    internal const string CoverageTraceDirectoryEnvironmentVariable =
        "THREADWAY_STRYKER_COVERAGE_TRACE_DIRECTORY";

    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    private static string? planDetailPath = NormalizePath(
        Environment.GetEnvironmentVariable(PlanDetailFileEnvironmentVariable));
    private static string? coverageTraceDirectory = NormalizePath(
        Environment.GetEnvironmentVariable(CoverageTraceDirectoryEnvironmentVariable));
    private static int planSegment;

    /// <summary>
    /// Appends one JSON line per plan segment describing every planned mutant.
    /// Assessing-test identifiers are recorded only for isolation-required
    /// mutants: they are the mutants whose set width needs a provenance trail,
    /// and recording every ordinary set would multiply the dump size without
    /// adding classification evidence.
    /// </summary>
    internal static void PlanBuilt(IReadOnlyList<List<IMutant>> groups)
    {
        if (planDetailPath is null)
        {
            return;
        }

        try
        {
            var mutants = groups
                .SelectMany(group => group)
                .Select(DescribeMutant)
                .ToList();
            var ordinaryGroupSizes = groups
                .Where(group => group.All(mutant =>
                    mutant is { IsStaticValue: false, MustBeTestedInIsolation: false } &&
                    mutant.AssessingTests?.IsEveryTest != true))
                .Select(group => group.Count)
                .ToList();

            lock (Sync)
            {
                planSegment++;
                var payload = new Dictionary<string, object?>
                {
                    ["segment"] = planSegment,
                    ["writtenAtUtc"] = DateTimeOffset.UtcNow,
                    ["ordinaryGroupSizes"] = ordinaryGroupSizes,
                    ["mutants"] = mutants,
                };
                File.AppendAllText(
                    planDetailPath,
                    JsonSerializer.Serialize(payload, SerializerOptions) + Environment.NewLine,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // Diagnostics must never change the mutation result.
        }
    }

    /// <summary>
    /// Preserves the raw per-test coverage records of one class-boundary
    /// capture, exactly as the xUnit lifecycle sink published them and before
    /// the runner widens static or outside-test coverage across the class.
    /// </summary>
    internal static void CoverageMapCaptured(
        string runnerId,
        string boundary,
        IReadOnlyList<string> requestedTestIds,
        IReadOnlyList<string> rawRecords)
    {
        if (coverageTraceDirectory is null)
        {
            return;
        }

        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["runnerId"] = runnerId,
                ["boundary"] = boundary,
                ["writtenAtUtc"] = DateTimeOffset.UtcNow,
                ["requestedTestIds"] = requestedTestIds,
                ["records"] = rawRecords,
            };
            Directory.CreateDirectory(coverageTraceDirectory);
            File.WriteAllText(
                Path.Combine(coverageTraceDirectory, $"coverage-{Guid.NewGuid():N}.json"),
                JsonSerializer.Serialize(payload, SerializerOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // Diagnostics must never change the mutation result.
        }
    }

    internal static void ConfigureForTests(
        string? planPath,
        string? traceDirectory)
    {
        lock (Sync)
        {
            planDetailPath = NormalizePath(planPath);
            coverageTraceDirectory = NormalizePath(traceDirectory);
            planSegment = 0;
        }
    }

    private static Dictionary<string, object?> DescribeMutant(IMutant mutant)
    {
        var assessingTests = mutant.AssessingTests;
        var isEveryTest = assessingTests?.IsEveryTest == true;
        var requiresIsolation = mutant.IsStaticValue || mutant.MustBeTestedInIsolation;
        var description = new Dictionary<string, object?>
        {
            ["id"] = mutant.Id,
            ["status"] = mutant.ResultStatus.ToString(),
            ["mutator"] = mutant.Mutation?.Type.ToString(),
            ["location"] = DescribeLocation(mutant.Mutation),
            ["isStaticValue"] = mutant.IsStaticValue,
            ["mustBeTestedInIsolation"] = mutant.MustBeTestedInIsolation,
            ["isEveryTest"] = isEveryTest,
            ["assessingTestCount"] = isEveryTest ? null : assessingTests?.Count,
            ["coveringTestCount"] = mutant.CoveringTests?.IsEveryTest == true
                ? null
                : mutant.CoveringTests?.Count,
        };
        if (requiresIsolation && assessingTests is not null && !isEveryTest)
        {
            description["assessingTests"] = assessingTests.GetIdentifiers().Order().ToList();
        }

        return description;
    }

    private static string? DescribeLocation(Mutation? mutation)
    {
        try
        {
            var location = mutation?.OriginalNode?.GetLocation();
            if (location is null)
            {
                return null;
            }

            var span = location.GetMappedLineSpan();
            return $"{span.Path}:{span.StartLinePosition.Line + 1}";
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }
}
