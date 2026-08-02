using Stryker.Abstractions;
using Stryker.Abstractions.Options;
using Stryker.Abstractions.Testing;

namespace Stryker.TestRunner.MicrosoftTestPlatform;

/// <summary>
/// Builds Stryker mutation batches without allowing process-isolated mutants to
/// consume test slots in ordinary reusable-host batches.
/// </summary>
public static class MutationBatchPlanner
{
    /// <summary>
    /// Upper bound on the union assessing-test count of a packed multi-mutant batch.
    /// A packed batch multiplexes mutants through one request via the per-test
    /// activation map, which forces serialized execution; the serial per-test cost
    /// keeps a batch at this bound to a few seconds. A mutant whose own set exceeds
    /// the bound goes to a singleton batch instead, where the runner activates it
    /// for the whole session and the host keeps its normal parallel execution.
    /// </summary>
    private const int MaximumSerialSessionTests = 256;
    private const int MaximumMutantsPerPackedSession = 16;

    /// <summary>
    /// Returns whether a planned group should use the broad-session concurrency gate.
    /// Broad ordinary single-mutant sessions retain xUnit's internal parallelism;
    /// allowing one such host per reported processor oversubscribes the machine and
    /// reduces throughput. The dedicated gate caps those sessions at half the runner
    /// pool atomically while narrow, packed, and process-isolated work can still use
    /// every worker.
    /// </summary>
    public static bool RequiresBroadSessionLimit(IReadOnlyList<IMutant> group) =>
        group.Count == 1 &&
        !RequiresProcessIsolation(group[0]) &&
        (group[0].AssessingTests.IsEveryTest ||
         group[0].AssessingTests.Count > MaximumSerialSessionTests);

    public static IEnumerable<List<IMutant>> Build(
        IStrykerOptions options,
        IReadOnlyCollection<IMutant> mutantsNotRun)
    {
        if (options.OptimizationMode.HasFlag(OptimizationModes.DisableMixMutants) ||
            !options.OptimizationMode.HasFlag(OptimizationModes.CoverageBasedTest))
        {
            var singleMutantGroups = mutantsNotRun
                .Select(mutant => new List<IMutant> { mutant })
                .ToList();
            ReportPlan(singleMutantGroups);
            return singleMutantGroups;
        }

        var groups = new List<List<IMutant>>(mutantsNotRun.Count);
        var remaining = mutantsNotRun
            .Where(mutant => mutant.ResultStatus == MutantStatus.Pending)
            .ToList();

        groups.AddRange(
            remaining
                .Where(mutant => mutant.AssessingTests.IsEveryTest)
                .Select(mutant => new List<IMutant> { mutant }));
        remaining.RemoveAll(mutant => mutant.AssessingTests.IsEveryTest);

        // Static and early-activation mutants require a fresh reload boundary, but disjoint
        // assessing sets may share that one fresh process. The activation map binds each test
        // to its mutant before its test lifecycle starts; disjoint coverage proves a test for one
        // mutant cannot initialize another packed mutant's static path. Keep isolation groups
        // separate from ordinary groups so a warm-host request never absorbs one accidentally.
        groups.AddRange(PackDisjointMutants(remaining.Where(RequiresProcessIsolation)));
        remaining.RemoveAll(RequiresProcessIsolation);

        groups.AddRange(
            remaining
                .Where(mutant => mutant.AssessingTests.Count > MaximumSerialSessionTests)
                .Select(mutant => new List<IMutant> { mutant }));
        remaining.RemoveAll(mutant => mutant.AssessingTests.Count > MaximumSerialSessionTests);

        groups.AddRange(PackDisjointMutants(remaining));

        ReportPlan(groups);
        return groups;
    }

    private static IEnumerable<List<IMutant>> PackDisjointMutants(IEnumerable<IMutant> candidates)
    {
        var remaining = candidates
            .OrderBy(mutant => mutant.AssessingTests.Count)
            .ToList();
        var groups = new List<List<IMutant>>();

        while (remaining.Count > 0)
        {
            var assessingTests = remaining[0].AssessingTests;
            var unionTestCount = remaining[0].AssessingTests.Count;
            var group = new List<IMutant> { remaining[0] };
            remaining.RemoveAt(0);

            for (var index = 0; index < remaining.Count; index++)
            {
                // An inconclusive packed request retries unresolved mutants one at a
                // time. Bound the group so one scheduler item cannot hide an
                // arbitrarily long serial retry tail from the worker pool.
                if (group.Count >= MaximumMutantsPerPackedSession)
                {
                    break;
                }

                var candidate = remaining[index];

                // Candidates are ordered by ascending set size, so the first one
                // that would push the disjoint union past the serial bound proves
                // every later candidate would too.
                if (unionTestCount + candidate.AssessingTests.Count > MaximumSerialSessionTests)
                {
                    break;
                }

                if (candidate.AssessingTests.ContainsAny(assessingTests))
                {
                    continue;
                }

                group.Add(candidate);
                remaining.RemoveAt(index--);
                assessingTests = assessingTests.Merge(candidate.AssessingTests);
                unionTestCount += candidate.AssessingTests.Count;
            }

            groups.Add(group);
        }

        return groups;
    }

    private static void ReportPlan(IReadOnlyList<List<IMutant>> groups)
    {
        var isolatedMutantCount = groups
            .SelectMany(group => group)
            .Count(RequiresProcessIsolation);
        var ordinaryGroups = groups
            .Where(group => group.All(mutant => !RequiresProcessIsolation(mutant)))
            .ToList();
    }

    internal static bool RequiresProcessIsolation(IMutant mutant) =>
        mutant.IsStaticValue || mutant.MustBeTestedInIsolation;
}
