using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Stryker.Abstractions;
using Stryker.Abstractions.Baseline;
using Stryker.Abstractions.Options;
using Stryker.Abstractions.ProjectComponents;
using Stryker.Abstractions.Reporting;
using Stryker.Core.Baseline.Providers;
using Stryker.Core.Baseline.Utils;
using Stryker.Utilities;
using Stryker.Utilities.Logging;

namespace Stryker.Core.MutantFilters;

public class BaselineMutantFilter : IMutantFilter
{
    internal const string ReusedResultReason = "Result based on previous run";

    private readonly IBaselineProvider _baselineProvider;
    private readonly IGitInfoProvider _gitInfoProvider;
    private readonly ILogger<BaselineMutantFilter> _logger;
    private readonly IBaselineMutantHelper _baselineMutantHelper;

    private readonly IStrykerOptions _options;
    private readonly IJsonReport _baseline;

    public MutantFilter Type => MutantFilter.Baseline;
    public string DisplayName => "baseline filter";

    public BaselineMutantFilter(IStrykerOptions options, IBaselineProvider baselineProvider = null,
        IGitInfoProvider gitInfoProvider = null, IBaselineMutantHelper baselineMutantHelper = null)
    {
        _logger = ApplicationLogging.LoggerFactory.CreateLogger<BaselineMutantFilter>();
        _baselineProvider = baselineProvider ?? BaselineProviderFactory.Create(options);
        _gitInfoProvider = gitInfoProvider ?? new GitInfoProvider(options);
        _baselineMutantHelper = baselineMutantHelper ?? new BaselineMutantHelper();

        _options = options;

        if (options.WithBaseline)
        {
            _baseline = GetBaselineAsync().Result;
        }
    }


    public IEnumerable<IMutant> FilterMutants(IEnumerable<IMutant> mutants, IReadOnlyFileLeaf file,
        IStrykerOptions options)
    {
        if (options.WithBaseline)
        {
            if (_baseline == null)
            {
                _logger.LogDebug(
                    "Returning all mutants on {RelativeFilePath} because there is no baseline available",
                    file.RelativePath);
            }
            else
            {
                UpdateMutantsWithBaselineStatus(mutants, file);
            }
        }

        return mutants;
    }

    private void UpdateMutantsWithBaselineStatus(IEnumerable<IMutant> mutants, IReadOnlyFileLeaf file)
    {
        if (!TryResolveBaselineFile(file, out var baselineFile))
        {
            return;
        }

        if (baselineFile is { })
        {
            foreach (var baselineMutant in baselineFile.Mutants)
            {
                var baselineMutantSourceCode =
                    _baselineMutantHelper.GetMutantSourceCode(baselineFile.Source, baselineMutant);

                if (string.IsNullOrEmpty(baselineMutantSourceCode))
                {
                    _logger.LogWarning(
                        "Unable to find mutant span in original baseline source code. This indicates a bug in stryker. Please report this on github.");
                    continue;
                }

                var matchingMutants =
                    _baselineMutantHelper.GetMutantMatchingSourceCode(mutants, baselineMutant,
                        baselineMutantSourceCode);

                SetMutantStatusToBaselineMutantStatus(baselineMutant, matchingMutants);
            }
        }
    }

    private bool TryResolveBaselineFile(IReadOnlyFileLeaf file, out ISourceFile baselineFile)
    {
        var normalizedPath = FilePathUtils.NormalizePathSeparators(file.RelativePath);
        if (_baseline.Files.TryGetValue(normalizedPath, out baselineFile))
        {
            return true;
        }

        // JSON reports historically persisted absolute source paths. A disk baseline restored on
        // another runner therefore has a different root even though it describes the same file.
        // Resolve the current file relative to the repository and require one unambiguous suffix
        // match. Ambiguous or unrelativizable paths fail closed and leave the mutants pending.
        var repositoryPath = _gitInfoProvider.RepositoryPath;
        var fullPath = string.IsNullOrWhiteSpace(file.FullPath)
            ? file.RelativePath
            : file.FullPath;
        if (string.IsNullOrWhiteSpace(repositoryPath) || string.IsNullOrWhiteSpace(fullPath))
        {
            baselineFile = null;
            return false;
        }

        var repositoryRelativePath = FilePathUtils.NormalizePathSeparators(
            Path.GetRelativePath(repositoryPath, fullPath))
            .TrimStart('.', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var suffix = Path.DirectorySeparatorChar + repositoryRelativePath;
        var matches = _baseline.Files
            .Where(entry =>
                FilePathUtils.NormalizePathSeparators(entry.Key)
                    .EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        if (matches.Count == 1)
        {
            baselineFile = matches[0].Value;
            return true;
        }

        baselineFile = null;
        return false;
    }

    private static void SetMutantStatusToBaselineMutantStatus(IJsonMutant baselineMutant,
        IEnumerable<IMutant> matchingMutants)
    {
        if (matchingMutants.Count() == 1)
        {
            var matchingMutant = matchingMutants.First();
            matchingMutant.ResultStatus = (MutantStatus)Enum.Parse(typeof(MutantStatus), baselineMutant.Status);
            matchingMutant.ResultStatusReason = ReusedResultReason;
        }
        else
        {
            foreach (var matchingMutant in matchingMutants)
            {
                matchingMutant.ResultStatus = MutantStatus.Pending;
                matchingMutant.ResultStatusReason = "Result based on previous run was inconclusive";
            }
        }
    }

    private async Task<IJsonReport> GetBaselineAsync()
    {
        var branchName = _gitInfoProvider.GetCurrentBranchName();

        var baselineLocation = $"baseline/{branchName}";

        var report = await _baselineProvider.Load(baselineLocation);

        if (report == null)
        {
            _logger.LogInformation(
                "We could not locate a baseline for branch {BranchName}, now trying fallback version {FallbackVersion}",
                branchName, _options.FallbackVersion);

            return await GetFallbackBaselineAsync();
        }

        _logger.LogInformation("Found baseline report for current branch {BranchName}", branchName);

        return report;
    }

    private async Task<IJsonReport> GetFallbackBaselineAsync(bool baseline = true)
    {
        var report = await _baselineProvider.Load($"{(baseline ? "baseline/" : "")}{_options.FallbackVersion}");

        if (report == null)
        {
            if (baseline)
            {
                _logger.LogDebug(
                    "We could not locate a baseline report for the fallback version. Now trying regular fallback version.");
                return await GetFallbackBaselineAsync(false);
            }

            _logger.LogInformation(
                "We could not locate a baseline report for the current branch, version or fallback version. Now running a complete test to establish a fresh baseline.");
            return null;
        }

        _logger.LogInformation("Found fallback report using version {FallbackVersion}", _options.FallbackVersion);

        return report;
    }
}
