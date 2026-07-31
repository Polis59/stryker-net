using System.Text.Json;
using Stryker.TestRunner.MicrosoftTestPlatform.Models;

namespace Stryker.TestRunner.MicrosoftTestPlatform.Tests;

public sealed class RunTestsRequestEncodingTests
{
    [Fact]
    public void RunRequestCarriesTheFilterUnderTheServersPropertyNames()
    {
        var runId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var request = new RunTestsRequest(
            runId,
            [new RunRequestTestNode("case-uid", "Example.SampleTests.Case")]);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request));
        var root = document.RootElement;

        // Microsoft.Testing.Platform's server binds exactly these names. A filter
        // sent under any other property is silently ignored and the server runs
        // the complete assembly, which breaks per-test mutation activation.
        Assert.Equal(runId.ToString(), root.GetProperty("runId").GetString());
        var test = Assert.Single(root.GetProperty("tests").EnumerateArray());
        Assert.Equal("case-uid", test.GetProperty("uid").GetString());
        Assert.Equal("Example.SampleTests.Case", test.GetProperty("display-name").GetString());
        Assert.Equal(2, test.EnumerateObject().Count());
    }
}
