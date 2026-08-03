using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Shouldly;
using Stryker.Abstractions;
using Stryker.Abstractions.Options;
using Stryker.Abstractions.ProjectComponents;
using Stryker.Configuration.Options;
using Stryker.Core.MutantFilters;
using Stryker.Core.Mutants;
using Stryker.Core.ProjectComponents.Csharp;

namespace Stryker.Core.UnitTest.MutantFilters;

[TestClass]
public class BroadcastMutantFilterTests
{
    [TestMethod]
    public void ShouldPreserveBaselineVerdictExcludedBySinceFilter()
    {
        var sinceFilter = new Mock<IMutantFilter>();
        sinceFilter.SetupGet(filter => filter.Type).Returns(MutantFilter.Since);
        sinceFilter.SetupGet(filter => filter.DisplayName).Returns("since filter");
        sinceFilter.Setup(filter => filter.FilterMutants(
                It.IsAny<IEnumerable<IMutant>>(),
                It.IsAny<IReadOnlyFileLeaf>(),
                It.IsAny<IStrykerOptions>()))
            .Returns([]);
        var mutant = new Mutant
        {
            ResultStatus = MutantStatus.Killed,
            ResultStatusReason = "Result based on previous run"
        };
        var target = new BroadcastMutantFilter([sinceFilter.Object]);

        var result = target.FilterMutants(
            [mutant],
            new CsharpFileLeaf(),
            new StrykerOptions { WithBaseline = true });

        result.ShouldBeEmpty();
        mutant.ResultStatus.ShouldBe(MutantStatus.Killed);
        mutant.ResultStatusReason.ShouldBe("Result based on previous run");
    }
}
