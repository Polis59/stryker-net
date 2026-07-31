using System;
using System.Diagnostics.CodeAnalysis;
using Stryker.Abstractions.Testing;
namespace Stryker.TestRunner.MicrosoftTestPlatform.Models;

[ExcludeFromCodeCoverage]
public sealed class MtpTestCase : ITestCase
{
    private readonly TestNode _testNode;
    public MtpTestCase(TestNode testNode)
    {
        _testNode = testNode;
    }

    // xUnit's default display name is the fully qualified method name; MTP exposes no richer
    // source identity, and consumers (e.g. test-to-source enrichment) dereference this.
    public string FullyQualifiedName => _testNode.DisplayName;
    public Uri Uri => new("executor://MicrosoftTestPlatform");
    public int LineNumber { get; }

    public string Source { get; }
    public string CodeFilePath => string.Empty;

    public string AssemblyPath { get; init; }

    public Guid Guid { get; }
    public string Name => _testNode.DisplayName;

    public string Id => _testNode.Uid;
}
