using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Stryker.TestRunner.MicrosoftTestPlatform;

internal sealed class CollectibleTestIsolationClient : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);
    private readonly string assembly;
    private readonly IReadOnlyDictionary<string, string?> environmentVariables;
    private readonly ILogger logger;
    private readonly string runnerId;
    private readonly SemaphoreSlim executionLock = new(1, 1);
    private Process? process;
    private NamedPipeClientStream? pipe;
    private StreamReader? reader;
    private StreamWriter? writer;
    private bool disposed;

    internal CollectibleTestIsolationClient(
        string assembly,
        IReadOnlyDictionary<string, string?> environmentVariables,
        ILogger logger,
        string runnerId)
    {
        this.assembly = assembly;
        this.environmentVariables = environmentVariables;
        this.logger = logger;
        this.runnerId = runnerId;
    }

    internal async Task<CollectibleIsolationResponse> ExecuteAsync(
        IReadOnlyCollection<string> testCaseIds,
        TimeSpan? timeout)
    {
        await executionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnsureStartedAsync().ConfigureAwait(false);
            // A collectible context always assesses one whole-context-activated mutant, so its
            // tests may run with xUnit's normal collection parallelism.
            var request = new CollectibleIsolationRequest(
                assembly,
                testCaseIds,
                Shutdown: false,
                Parallel: true);
            await writer!.WriteLineAsync(
                JsonSerializer.Serialize(request, SerializerOptions))
                .ConfigureAwait(false);

            var responseTimeout =
                (timeout ?? TimeSpan.FromMinutes(5)) + TimeSpan.FromSeconds(5);
            string? responseJson;
            try
            {
                responseJson = await reader!.ReadLineAsync()
                    .WaitAsync(responseTimeout)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                StopHost("request-timeout");
                return CollectibleIsolationResponse.TimedOut();
            }

            if (responseJson is null)
            {
                var exitCode = process is { HasExited: true }
                    ? process.ExitCode.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                    : "unknown";
                StopHost("exited-before-response");
                return CollectibleIsolationResponse.RuntimeError(
                    $"The collectible isolation host exited before responding (exit code {exitCode}).");
            }

            var response = JsonSerializer.Deserialize<CollectibleIsolationResponse>(
                responseJson,
                SerializerOptions);
            if (response is null)
            {
                StopHost("empty-response");
                return CollectibleIsolationResponse.RuntimeError(
                    "The collectible isolation host returned an empty response.");
            }

            if (!response.Unloaded)
            {
                StopHost("context-not-unloaded");
            }

            return response;
        }
        catch (Exception exception)
        {
            StopHost("request-exception");
            return CollectibleIsolationResponse.RuntimeError(
                exception.GetBaseException().ToString());
        }
        finally
        {
            executionLock.Release();
        }
    }

    private async Task EnsureStartedAsync()
    {
        if (process is { HasExited: false } && pipe is { IsConnected: true })
        {
            return;
        }

        StopHost("replacing-unavailable-host");

        var hostAssembly = Path.Combine(
            Path.GetDirectoryName(
                typeof(CollectibleTestIsolationClient).Assembly.Location)!,
            "isolation-host",
            "Stryker.TestIsolationHost.dll");
        if (!File.Exists(hostAssembly))
        {
            throw new FileNotFoundException(
                "The prepared Stryker payload does not contain the collectible isolation host.",
                hostAssembly);
        }

        var pipeName = $"threadway-stryker-isolation-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(assembly)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(hostAssembly);
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        foreach (var (name, value) in environmentVariables)
        {
            startInfo.Environment[name] = value;
        }

        process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "The collectible isolation host process could not be started.");
        MutationCampaignProgressReporter.IsolationHostStarted(
            runnerId,
            assembly,
            process.Id);
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                logger.LogDebug(
                    "{RunnerId}: Isolation host: {Message}",
                    runnerId,
                    eventArgs.Data);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                logger.LogDebug(
                    "{RunnerId}: Isolation host error: {Message}",
                    runnerId,
                    eventArgs.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(30_000).ConfigureAwait(false);
        reader = new StreamReader(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        writer = new StreamWriter(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            leaveOpen: true)
        {
            AutoFlush = true,
        };
    }

    private void StopHost(string reason)
    {
        writer?.Dispose();
        writer = null;
        reader?.Dispose();
        reader = null;
        pipe?.Dispose();
        pipe = null;

        if (process is not null)
        {
            var processId = process.Id;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5_000);
                }
            }
            catch (Exception)
            {
                // The broker may already have exited after a fatal test failure.
            }

            process.Dispose();
            process = null;
            MutationCampaignProgressReporter.IsolationHostStopped(processId, reason);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        StopHost("client-disposed");
        executionLock.Dispose();
    }
}

internal sealed record CollectibleIsolationRequest(
    string AssemblyPath,
    IReadOnlyCollection<string> TestCaseIds,
    bool Shutdown,
    bool Parallel = false);

internal sealed record CollectibleIsolationTestResult(
    string TestCaseId,
    string State,
    string? Message);

internal sealed record CollectibleIsolationResponse(
    IReadOnlyList<CollectibleIsolationTestResult> Tests,
    string? Error,
    long DurationTicks,
    bool Unloaded,
    bool SessionTimedOut = false)
{
    internal static CollectibleIsolationResponse TimedOut() =>
        new([], null, 0, Unloaded: false, SessionTimedOut: true);

    internal static CollectibleIsolationResponse RuntimeError(string error) =>
        new([], error, 0, Unloaded: false);
}
