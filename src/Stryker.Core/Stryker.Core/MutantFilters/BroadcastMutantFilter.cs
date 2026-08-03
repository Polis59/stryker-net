using System.Collections.Generic;
using System.Linq;
using Stryker.Abstractions;
using Stryker.Abstractions.Options;
using Stryker.Abstractions.ProjectComponents;

namespace Stryker.Core.MutantFilters;

public class BroadcastMutantFilter : IMutantFilter
{
    public MutantFilter Type => MutantFilter.Broadcast;
    public IEnumerable<IMutantFilter> MutantFilters { get; }

    public BroadcastMutantFilter(IEnumerable<IMutantFilter> mutantFilters) => MutantFilters = mutantFilters;

    public string DisplayName => "broadcast filter";

    public IEnumerable<IMutant> FilterMutants(IEnumerable<IMutant> mutants, IReadOnlyFileLeaf file, IStrykerOptions options)
    {
        IEnumerable<IMutant> mutantsToTest = mutants.Where(m => m.ResultStatus is not MutantStatus.Ignored).ToList();

        foreach (var mutantFilter in MutantFilters)
        {
            // These mutants should be tested according to current filter
            var remainingMutantsToTest = mutantFilter.FilterMutants(mutantsToTest, file, options);

            // All mutants that weren't filtered out by a previous filter but were by the current filter are set to Ignored
            foreach (var skippedMutant in mutantsToTest.Except(remainingMutantsToTest))
            {
                // Baseline mode deliberately excludes unchanged mutants from execution after
                // restoring their prior verdict. Preserve that evidence in the complete report;
                // replacing it with Ignored would make the baseline both unverifiable and
                // unusable by the next run.
                if (mutantFilter.Type == MutantFilter.Since &&
                    options.WithBaseline &&
                    skippedMutant.ResultStatusReason == BaselineMutantFilter.ReusedResultReason)
                {
                    continue;
                }

                skippedMutant.ResultStatus = MutantStatus.Ignored;
                skippedMutant.ResultStatusReason = $"Removed by {mutantFilter.DisplayName}";
            }

            mutantsToTest = remainingMutantsToTest;
        }

        return mutantsToTest;
    }
}
