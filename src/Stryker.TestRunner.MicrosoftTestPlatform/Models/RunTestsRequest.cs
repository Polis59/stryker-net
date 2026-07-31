using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Stryker.TestRunner.MicrosoftTestPlatform.Models;

/// <summary>
/// The `testing/runTests` request. Microsoft.Testing.Platform's server reads the
/// test filter from the `tests` property; a request that carries the filter
/// under any other name is treated as unfiltered and the server runs the
/// complete assembly. Each entry binds `uid` and `display-name` exactly as the
/// server's JSON binder expects.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed record RunTestsRequest(
    [property:JsonPropertyName("runId")]
    Guid RunId,
    [property:JsonPropertyName("tests")]
    RunRequestTestNode[]? Tests = null);

[ExcludeFromCodeCoverage]
public sealed record RunRequestTestNode(
    [property:JsonPropertyName("uid")]
    string Uid,
    [property:JsonPropertyName("display-name")]
    string DisplayName);
