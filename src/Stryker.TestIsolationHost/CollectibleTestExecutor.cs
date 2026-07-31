using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using System.Text;
using Xunit.Runner.Common;
using Xunit.Runner.InProc.SystemConsole;
using Xunit.Sdk;
using Xunit.v3;

namespace Stryker.TestIsolationHost;

public static class CollectibleTestExecutor
{
    public static string Execute(
        string assemblyPath,
        string testCaseIdsPayload,
        string executionMode)
    {
        var request = new IsolationRequest(
            assemblyPath,
            testCaseIdsPayload.Length == 0
                ? []
                : testCaseIdsPayload.Split('\n'),
            Shutdown: false,
            Parallel: string.Equals(executionMode, "parallel", StringComparison.Ordinal));
        return SerializeResponse(
            ExecuteAsync(request).GetAwaiter().GetResult());
    }

    private static async Task<IsolationResponse> ExecuteAsync(
        IsolationRequest request)
    {
        var stopwatch = Stopwatch.StartNew();
        var assemblyPath = Path.GetFullPath(request.AssemblyPath!);
        var loadContext =
            AssemblyLoadContext.GetLoadContext(
                typeof(CollectibleTestExecutor).Assembly)
            ?? throw new InvalidOperationException(
                "The collectible executor does not own an assembly load context.");
        var testAssembly = loadContext.LoadFromAssemblyPath(assemblyPath);
        var targetFramework =
            testAssembly
                .GetCustomAttribute<TargetFrameworkAttribute>()
                ?.FrameworkName;

        var project = new XunitProject();
        var projectAssembly = new XunitProjectAssembly(
            project,
            assemblyPath,
            new AssemblyMetadata(3, targetFramework))
        {
            Assembly = testAssembly,
        };
        ConfigReader_Json.Load(
            projectAssembly.Configuration,
            projectAssembly.AssemblyFileName,
            projectAssembly.ConfigFileName);
        // MTP server mode forces pre-enumeration, and xUnit includes that
        // choice in theory case identity. Match it so MTP selections remain
        // valid inside the collectible runner.
        projectAssembly.Configuration.PreEnumerateTheories = true;
        // A collectible context assesses exactly one mutant, activated for the whole context
        // through the mutant file — no per-test switching happens here, so tests may run with
        // xUnit's normal collection parallelism. Serial execution multiplies a full-suite
        // static-mutant session's wall time by the parallelism factor for no correctness gain.
        projectAssembly.Configuration.ParallelizeAssembly = false;
        projectAssembly.Configuration.ParallelizeTestCollections = request.Parallel;
        projectAssembly.Configuration.SynchronousMessageReporting = !request.Parallel;
        projectAssembly.Configuration.ShadowCopy = false;
        project.Add(projectAssembly);

        using var cancellation = new CancellationTokenSource();
        var runner = new ProjectAssemblyRunner(
            testAssembly,
            AutomatedMode.Off,
            NullSourceInformationProvider.Instance,
            cancellation);
        var sink = new ResultSink();
        ITestPipelineStartup? pipelineStartup = null;
        try
        {
            pipelineStartup = await ProjectAssemblyRunner.InvokePipelineStartup(
                testAssembly,
                diagnosticMessageSink: null);
            await runner.Run(
                projectAssembly,
                sink,
                diagnosticMessageSink: null,
                NullRunnerLogger.Instance,
                pipelineStartup,
                request.TestCaseIds.ToHashSet(StringComparer.Ordinal));
        }
        finally
        {
            if (pipelineStartup is not null)
            {
                await pipelineStartup.StopAsync();
            }
        }

        stopwatch.Stop();
        return new IsolationResponse(
            sink.Results,
            sink.Errors.Count == 0
                ? null
                : string.Join(Environment.NewLine, sink.Errors),
            stopwatch.Elapsed.Ticks,
            Unloaded: false);
    }

    private static string SerializeResponse(IsolationResponse response)
    {
        var lines = new List<string>(response.Tests.Count + 3)
        {
            response.DurationTicks.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            response.Error is null ? string.Empty : Encode(response.Error),
            response.Tests.Count.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
        };
        lines.AddRange(response.Tests.Select(result =>
            string.Join(
                '\t',
                Encode(result.TestCaseId),
                result.State,
                result.Message is null ? string.Empty : Encode(result.Message))));
        return string.Join('\n', lines);
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private sealed class ResultSink : IMessageSink
    {
        // Parallel test collections deliver messages concurrently.
        private readonly object _sync = new();

        public List<IsolationTestResult> Results { get; } = [];
        public List<string> Errors { get; } = [];

        public bool OnMessage(IMessageSinkMessage message)
        {
            lock (_sync)
            {
                return OnMessageLocked(message);
            }
        }

        private bool OnMessageLocked(IMessageSinkMessage message)
        {
            switch (message)
            {
                case ITestFailed failed:
                    Results.Add(new IsolationTestResult(
                        failed.TestCaseUniqueID,
                        "failed",
                        string.Join(Environment.NewLine, failed.Messages)));
                    break;
                case ITestPassed passed:
                    Results.Add(new IsolationTestResult(
                        passed.TestCaseUniqueID,
                        "passed",
                        null));
                    break;
                case ITestSkipped skipped:
                    Results.Add(new IsolationTestResult(
                        skipped.TestCaseUniqueID,
                        "skipped",
                        skipped.Reason));
                    break;
                case ITestNotRun notRun:
                    Results.Add(new IsolationTestResult(
                        notRun.TestCaseUniqueID,
                        "not-run",
                        null));
                    break;
                case IErrorMessage error:
                    Errors.Add(string.Join(Environment.NewLine, error.Messages));
                    break;
            }

            return true;
        }
    }

    private sealed class NullRunnerLogger : IRunnerLogger
    {
        public static NullRunnerLogger Instance { get; } = new();
        public object LockObject { get; } = new();
        public void LogError(StackFrameInfo stackFrame, string message)
        {
        }

        public void LogImportantMessage(StackFrameInfo stackFrame, string message)
        {
        }

        public void LogMessage(StackFrameInfo stackFrame, string message)
        {
        }

        public void LogRaw(string message)
        {
        }

        public void LogWarning(StackFrameInfo stackFrame, string message)
        {
        }

        public void WaitForAcknowledgment()
        {
        }
    }
}
