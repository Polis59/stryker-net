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

        // Static and early-activation mutants require a distinct reload
        // boundary. Keeping them out of the greedy ordinary packer prevents
        // their broad coverage sets from fragmenting reusable-host batches.
        groups.AddRange(
            remaining
                .Where(RequiresProcessIsolation)
                .Select(mutant => new List<IMutant> { mutant }));
        remaining.RemoveAll(RequiresProcessIsolation);

        if (options.TestRunner == Stryker.Abstractions.Options.TestRunner.MicrosoftTestPlatform)
        {
            // The MTP runner executes each batch in waves: every wave publishes a fresh
            // test-to-mutant activation map, so disjoint assessing sets are only required inside
            // one wave request, never across a whole batch — a shared test simply serves its
            // mutants in successive waves. Disjoint packing would fragment hub-covered mutants
            // into thousands of tiny batches, each paying a full fixture initialization; even
            // chunks sized to keep every runner busy pay it a handful of times in total.
            var chunkCount = Math.Max(1, options.Concurrency * 2);
            var chunkSize = Math.Max(1, (remaining.Count + chunkCount - 1) / chunkCount);
            groups.AddRange(remaining.Chunk(chunkSize).Select(chunk => chunk.ToList()));

            ReportPlan(groups);
            return groups;
        }

        remaining = remaining
            .OrderBy(mutant => mutant.AssessingTests.Count)
            .ToList();

        while (remaining.Count > 0)
        {
            var assessingTests = remaining[0].AssessingTests;
            var group = new List<IMutant> { remaining[0] };
            remaining.RemoveAt(0);

            for (var index = 0; index < remaining.Count; index++)
            {
                var candidate = remaining[index];
                if (candidate.AssessingTests.ContainsAny(assessingTests))
                {
                    continue;
                }

                group.Add(candidate);
                remaining.RemoveAt(index--);
                assessingTests = assessingTests.Merge(candidate.AssessingTests);
            }

            groups.Add(group);
        }

        ReportPlan(groups);
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
        MutationCampaignProgressReporter.MutationPlanBuilt(
            groups.Sum(group => group.Count),
            isolatedMutantCount,
            ordinaryGroups.Sum(group => group.Count),
            ordinaryGroups.Count);
        MutationCampaignDiagnostics.PlanBuilt(groups);
    }

    private static bool RequiresProcessIsolation(IMutant mutant) =>
        mutant.IsStaticValue || mutant.MustBeTestedInIsolation;
}
