using Microsoft.Extensions.Logging;
using Stryker.Abstractions.Options;
using Stryker.TestRunner.MicrosoftTestPlatform.Models;

namespace Stryker.TestRunner.MicrosoftTestPlatform;

/// <summary>
/// Manages a persistent test server connection for a single assembly.
/// The server process is started once and reused across multiple test runs.
/// </summary>
internal sealed class AssemblyTestServer : IDisposable
{
    private readonly string _assembly;
    private readonly Dictionary<string, string?> _environmentVariables;
    private readonly ILogger _logger;
    private readonly string _runnerId;
    private readonly IStrykerOptions? _options;
    private readonly ITestServerConnectionFactory _connectionFactory;
    private ITestServerListener? _listener;
    private ITestServerProcess? _process;
    private Stream? _stream;
    private IDisposable? _connection;
    private ITestingPlatformClient? _client;
    private bool _isInitialized;
    private bool _disposed;

    public AssemblyTestServer(
        string assembly,
        Dictionary<string, string?> environmentVariables,
        ILogger logger,
        string runnerId,
        IStrykerOptions? options = null,
        ITestServerConnectionFactory? connectionFactory = null)
    {
        _assembly = assembly;
        _environmentVariables = environmentVariables;
        _logger = logger;
        _runnerId = runnerId;
        _options = options;
        _connectionFactory = connectionFactory ?? new DefaultTestServerConnectionFactory(options);
    }

    public bool IsInitialized => _isInitialized;

    /// <summary>
    /// True when the server has been initialized and its underlying process is still running.
    /// A test host that crashed mid-run (e.g. a mutation causing a fatal fault such as a
    /// <see cref="StackOverflowException"/>) leaves <see cref="IsInitialized"/> true while the
    /// process is gone; this flag detects that so the server can be recreated instead of reused.
    /// </summary>
    public bool IsAlive => _isInitialized && !_disposed && _process is { HasExited: false };

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized)
        {
            return true;
        }

        try
        {
            var (listener, port) = _connectionFactory.CreateListener();
            _listener = listener;

            _process = _connectionFactory.StartProcess(_assembly, port, _environmentVariables);

            var acceptTask = _listener.AcceptConnectionAsync(cancellationToken);
            var connectionTimeout = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            var completedTask = await Task.WhenAny(_process.WaitForExitAsync(), acceptTask, connectionTimeout).ConfigureAwait(false);

            if (completedTask == connectionTimeout)
            {
                _logger.LogDebug("{RunnerId}: Timeout waiting for test server connection for {Assembly}", _runnerId, _assembly);
                await StopAsync().ConfigureAwait(false);
                return false;
            }

            if (_process.HasExited)
            {
                _logger.LogDebug("{RunnerId}: Test process exited prematurely for {Assembly}", _runnerId, _assembly);
                await StopAsync().ConfigureAwait(false);
                return false;
            }

            (_stream, _connection) = await acceptTask.ConfigureAwait(false);

            var rpcLogFilePath = BuildRpcLogFilePath();
            _client = _connectionFactory.CreateClient(_stream, _process.ProcessHandle, _logger, rpcLogFilePath);

            await _client.InitializeAsync().ConfigureAwait(false);
            _isInitialized = true;

            _logger.LogDebug("{RunnerId}: Test server started successfully for {Assembly}", _runnerId, _assembly);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{RunnerId}: Failed to start test server for {Assembly}", _runnerId, _assembly);
            await StopAsync().ConfigureAwait(false);
            return false;
        }
    }

    public async Task<List<TestNode>> DiscoverTestsAsync()
    {
        if (!_isInitialized || _client is null)
        {
            throw new InvalidOperationException("Server not initialized. Call StartAsync first.");
        }

        var discoveryId = Guid.NewGuid();
        List<TestNodeUpdate> discoveredResults = [];

        var discoverTestsResponse = await _client.DiscoverTestsAsync(discoveryId, updates =>
        {
            discoveredResults.AddRange(updates);
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        await discoverTestsResponse.WaitCompletionAsync().ConfigureAwait(false);

        return discoveredResults
            .Where(x => x.Node.ExecutionState is TestNodeStates.Discovered)
            .Select(x => x.Node)
            .ToList();
    }

    public async Task<List<TestNodeUpdate>> RunTestsAsync(TestNode[]? testsToRun)
    {
        var (results, _) = await RunTestsAsync(testsToRun, timeout: null).ConfigureAwait(false);
        return results;
    }

    public async Task<(List<TestNodeUpdate> Results, bool TimedOut)> RunTestsAsync(
        TestNode[]? testsToRun,
        TimeSpan? timeout,
        Func<TestNodeUpdate, bool>? bailPredicate = null)
    {
        if (!_isInitialized || _client is null)
        {
            throw new InvalidOperationException("Server not initialized. Call StartAsync first.");
        }

        var runId = Guid.NewGuid();
        var stageStopwatch = System.Diagnostics.Stopwatch.StartNew();
        long firstUpdateMs = -1;
        var testResults = new System.Collections.Concurrent.ConcurrentBag<TestNodeUpdate>();

        // Bail: when a streamed update proves the session's mutant is already killed, cancel the
        // run request. StreamJsonRpc turns the cancellation into a notification the test server
        // honours; the results collected so far carry the killing test. Should the server ignore
        // the cancellation, the RPC call simply completes normally and nothing is lost but time.
        using var bailSource = new CancellationTokenSource();
        var bailed = false;
        var stalled = false;
        var lastUpdateTicks = Environment.TickCount64;

        Func<TestNodeUpdate[], Task> onUpdate = updates =>
        {
            if (firstUpdateMs < 0)
            {
                firstUpdateMs = stageStopwatch.ElapsedMilliseconds;
            }

            Volatile.Write(ref lastUpdateTicks, Environment.TickCount64);

            foreach (var update in updates)
            {
                testResults.Add(update);
            }

            if (bailPredicate is not null && !bailed && updates.Any(bailPredicate))
            {
                bailed = true;
                _logger.LogDebug("{RunnerId}: Bailing out of test run for {Assembly}: a test already failed", _runnerId, _assembly);
                bailSource.Cancel();
            }

            return Task.CompletedTask;
        };

        if (timeout.HasValue)
        {
            // Stall detection: a healthy mutant session streams results continuously (first
            // update within a second, then a steady flow), while a hanging mutant goes silent.
            // Cancelling on silence converts a hang's cost from the whole session budget to
            // roughly the stall window, with the same honest Timeout verdict. The window is
            // wider before the first update to leave room for a cold host's session build.
            using var stallMonitorSource = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                while (!stallMonitorSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(1000, stallMonitorSource.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    var silenceMs = Environment.TickCount64 - Volatile.Read(ref lastUpdateTicks);
                    var limitMs = Interlocked.Read(ref firstUpdateMs) < 0 ? 30_000 : 10_000;
                    if (silenceMs > limitMs)
                    {
                        stalled = true;
                        _logger.LogDebug(
                            "{RunnerId}: Test run stalled ({SilenceMs} ms without an update) for {Assembly}; cancelling",
                            _runnerId, silenceMs, _assembly);
                        bailSource.Cancel();
                        return;
                    }
                }
            });

            ResponseListener executeTestsResponse;
            try
            {
                // The RPC call itself can block when the server is stuck (e.g. infinite loop in mutated code)
                executeTestsResponse = await _client.RunTestsAsync(runId, onUpdate, testsToRun, timeout.Value, bailSource.Token)
                    .WaitAsync(timeout.Value).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                _logger.LogDebug(ex, "{RunnerId}: Test run RPC call timed out for {Assembly}", _runnerId, _assembly);
                return (testResults.ToList(), true);
            }
            catch (OperationCanceledException) when (bailed)
            {
                return (testResults.ToList(), false);
            }
            catch (OperationCanceledException ex) when (stalled)
            {
                _logger.LogDebug(ex, "{RunnerId}: Test run cancelled after stalling for {Assembly}", _runnerId, _assembly);
                return (testResults.ToList(), true);
            }
            catch (OperationCanceledException ex)
            {
                // The client's backstop cancellation window expired. That is a timeout verdict,
                // not a crash: reporting it as an exception would route the batch through the
                // crash-retry path and burn the whole budget a second time.
                _logger.LogDebug(ex, "{RunnerId}: Test run RPC call was cancelled by its backstop window for {Assembly}", _runnerId, _assembly);
                return (testResults.ToList(), true);
            }

            var completionTask = executeTestsResponse.WaitCompletionAsync(timeout.Value, bailSource.Token);
            await Task.WhenAny(completionTask, HostExitAsync()).ConfigureAwait(false);
            if (bailed)
            {
                return (testResults.ToList(), false);
            }

            if (stalled)
            {
                return (testResults.ToList(), true);
            }

            ThrowIfHostCrashed(completionTask);

            bool completed;
            try
            {
                completed = await completionTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stalled)
            {
                return (testResults.ToList(), true);
            }

            _logger.LogInformation("{RunnerId}: RUNSTAGE firstUpdateMs={FirstUpdate} totalMs={Total} results={Results}",
                _runnerId, firstUpdateMs, stageStopwatch.ElapsedMilliseconds, testResults.Count);
            return (testResults.ToList(), !completed);
        }

        ResponseListener response;
        try
        {
            response = await _client.RunTestsAsync(runId, onUpdate, testsToRun, null, bailSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (bailed)
        {
            return (testResults.ToList(), false);
        }

        var responseCompletion = response.WaitCompletionAsync();
        await Task.WhenAny(responseCompletion, HostExitAsync()).ConfigureAwait(false);
        if (bailed)
        {
            return (testResults.ToList(), false);
        }

        ThrowIfHostCrashed(responseCompletion);

        await responseCompletion.ConfigureAwait(false);
        _logger.LogInformation("{RunnerId}: RUNSTAGE firstUpdateMs={FirstUpdate} totalMs={Total} results={Results}",
            _runnerId, firstUpdateMs, stageStopwatch.ElapsedMilliseconds, testResults.Count);
        return (testResults.ToList(), false);
    }

    /// <summary>
    /// Throws a <see cref="Stryker.TestRunner.TestHostCrashedException"/> when the test host process has exited before the
    /// run completed. A crashed host never sends a completion signal, so without this check the run would
    /// otherwise wait out the full timeout and be misreported as a timeout instead of a runtime error.
    /// </summary>
    /// <summary>
    /// A task that completes when the test host process exits, or never when the server
    /// runs without a real process (unit-test doubles drive `RunTestsAsync` directly).
    /// The crash race in <see cref="ThrowIfHostCrashed"/> is likewise process-optional.
    /// </summary>
    private Task HostExitAsync() =>
        _process?.WaitForExitAsync() ?? new TaskCompletionSource().Task;

    private void ThrowIfHostCrashed(Task runCompletion)
    {
        if (_process is { HasExited: true } && !runCompletion.IsCompletedSuccessfully)
        {
            _logger.LogDebug("{RunnerId}: Test host for {Assembly} exited unexpectedly during the test run", _runnerId, _assembly);
            throw new Stryker.TestRunner.TestHostCrashedException($"The test host for {_assembly} exited unexpectedly during the test run.");
        }
    }

    public async Task RestartAsync(bool force = false)
    {
        await StopAsync(force).ConfigureAwait(false);
        await StartAsync().ConfigureAwait(false);
    }

    public async Task StopAsync(bool force = false)
    {
        if (force)
        {
            _logger.LogDebug("{RunnerId}: Force-killing test server process for {Assembly}", _runnerId, _assembly);
            _process?.ProcessHandle.Kill();
        }
        else if (_client is not null)
        {
            try
            {
                await _client.ExitAsync().ConfigureAwait(false);
                // Coverage data must be flushed before disposing resources
                var timeout = TimeSpan.FromSeconds(30);
                await _client.WaitServerProcessExitAsync().WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                _logger.LogWarning(exception, "{RunnerId}: Test server process for {Assembly} did not exit within the expected time. Killing forcefully.", _runnerId, _assembly);
                _process?.ProcessHandle.Kill();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "{RunnerId}: Test server process for {Assembly} could not be stopped gracefully.", _runnerId, _assembly);
            }
        }

        _listener?.Stop();
        _listener?.Dispose();
        _listener = null;
        _client?.Dispose();
        _client = null;
        _stream?.Dispose();
        _stream = null;
        _connection?.Dispose();
        _connection = null;
        try
        {
            _process?.Dispose();
        }
        catch (Exception)
        {
            // Process disposal can fail if kill/cleanup didn't complete in time
        }
        _process = null;
        _isInitialized = false;
    }

    /// <summary>
    /// Returns a per-instance RPC trace log path when log-to-file is enabled, or null otherwise.
    /// </summary>
    private string? BuildRpcLogFilePath()
    {
        if (_options?.LogOptions.LogToFile != true || string.IsNullOrEmpty(_options.OutputPath))
        {
            return null;
        }

        var logsDirectory = Path.Combine(_options.OutputPath, "logs", "test-servers");
        var fileName = $"rpc-{Path.GetFileNameWithoutExtension(_assembly)}-{_runnerId}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fffffff}.log";
        return Path.Combine(logsDirectory, fileName);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopAsync().GetAwaiter().GetResult();
    }
}
