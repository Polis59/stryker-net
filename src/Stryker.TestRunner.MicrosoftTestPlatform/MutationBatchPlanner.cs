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
    private const int MaximumMutantsPerOrdinaryWaveBatch = 32;

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

        // Static and early-activation mutants require a fresh reload boundary, but disjoint
        // assessing sets may share that one fresh process. The activation map binds each test
        // to its mutant before its test lifecycle starts; disjoint coverage proves a test for one
        // mutant cannot initialize another packed mutant's static path. Keep isolation groups
        // separate from ordinary groups so a warm-host request never absorbs one accidentally.
        // An isolation mutant assessed by every test cannot share a fresh request because its set
        // necessarily overlaps every sibling; it remains a singleton.
        groups.AddRange(
            remaining
                .Where(mutant => RequiresProcessIsolation(mutant) && mutant.AssessingTests.IsEveryTest)
                .Select(mutant => new List<IMutant> { mutant }));
        groups.AddRange(PackDisjointMutants(
            remaining.Where(mutant =>
                RequiresProcessIsolation(mutant) && !mutant.AssessingTests.IsEveryTest)));
        remaining.RemoveAll(RequiresProcessIsolation);

        // Ordinary mutants may overlap because the MTP runner advances a batch in waves.
        // A test contested by several mutants is assigned to one of them in the current wave
        // and remains available to the others in later waves. Keep at least twice as many
        // groups as workers, but cap each group so one coverage-heavy batch cannot retain a
        // worker for the rest of the campaign while the other workers drain and go idle.
        if (remaining.Count > 0)
        {
            var chunkCount = Math.Min(remaining.Count, Math.Max(
                Math.Max(1, options.Concurrency) * 2,
                (remaining.Count + MaximumMutantsPerOrdinaryWaveBatch - 1) /
                MaximumMutantsPerOrdinaryWaveBatch));
            var mutationPriorities = SingleMicrosoftTestPlatformRunner
                .LoadIsolationMutationPriorities(Environment.GetEnvironmentVariable(
                    SingleMicrosoftTestPlatformRunner.IsolationMutationProfileFileVariable));
            string? SelectKillerFamily(IMutant mutant)
            {
                if (mutationPriorities.Count == 0)
                {
                    return null;
                }

                return mutationPriorities.TryGetValue(
                    SingleMicrosoftTestPlatformRunner.BuildMutationProfileKey(mutant),
                    out var priorities)
                    ? priorities.MaxBy(priority => priority.Value).Key
                    : null;
            }
            groups.AddRange(DistributeOrdinaryMutants(
                remaining,
                chunkCount,
                SelectKillerFamily));
        }

        ReportPlan(groups);
        return groups;
    }

    internal static IReadOnlyList<List<IMutant>> DistributeOrdinaryMutants(
        IReadOnlyCollection<IMutant> mutants,
        int groupCount,
        Func<IMutant, string?> killerFamilySelector)
    {
        if (groupCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(groupCount));
        }

        var buckets = Enumerable.Range(0, Math.Min(groupCount, mutants.Count))
            .Select(_ => new List<IMutant>())
            .ToList();
        var killerFamilies = buckets
            .Select(_ => new HashSet<string>(StringComparer.Ordinal))
            .ToList();
        var targetSize = (mutants.Count + buckets.Count - 1) / buckets.Count;
        var candidates = mutants
            .Select(mutant => (Mutant: mutant, Killer: killerFamilySelector(mutant)))
            .OrderBy(candidate => candidate.Killer is null)
            .ThenBy(candidate => candidate.Killer, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Mutant.Id)
            .ToList();

        foreach (var candidate in candidates)
        {
            var available = Enumerable.Range(0, buckets.Count)
                .Where(index => buckets[index].Count < targetSize)
                .ToList();
            var withoutSameKiller = candidate.Killer is null
                ? available
                : available
                    .Where(index => !killerFamilies[index].Contains(candidate.Killer))
                    .ToList();
            var target = (withoutSameKiller.Count > 0 ? withoutSameKiller : available)
                .OrderBy(index => buckets[index].Count)
                .ThenBy(index => index)
                .First();
            buckets[target].Add(candidate.Mutant);
            if (candidate.Killer is not null)
            {
                killerFamilies[target].Add(candidate.Killer);
            }
        }

        return buckets;
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
