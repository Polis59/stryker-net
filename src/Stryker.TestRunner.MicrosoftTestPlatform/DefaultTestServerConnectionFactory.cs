using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using CliWrap;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;
using Stryker.Abstractions.Options;
using Stryker.TestRunner.MicrosoftTestPlatform.Models;
using Stryker.TestRunner.MicrosoftTestPlatform.RPC;

namespace Stryker.TestRunner.MicrosoftTestPlatform;

/// <summary>
/// Default implementation that creates TCP connections and starts processes via CliWrap.
/// </summary>
internal sealed class DefaultTestServerConnectionFactory : ITestServerConnectionFactory
{
    private readonly string? _outputPath;
    private readonly bool _logToFile;
    private readonly string[] _filterArguments;

    public DefaultTestServerConnectionFactory(IStrykerOptions? options = null)
    {
        _outputPath = options?.OutputPath;
        _logToFile = options?.LogOptions.LogToFile ?? false;
        _filterArguments = TranslateTestCaseFilter(options?.TestCaseFilter);
    }

    /// <summary>
    /// Translates the VSTest-style `test-case-filter` option into the trait-exclusion
    /// arguments the Microsoft.Testing.Platform test host understands, so an excluded
    /// category never reaches discovery or execution. Only conjunctions of
    /// `Category!=value` clauses have an exact translation; any other clause shape is
    /// refused rather than silently ignored, because a filter that stops filtering
    /// would run tests the campaign's environment cannot support.
    /// </summary>
    private static string[] TranslateTestCaseFilter(string? testCaseFilter)
    {
        if (string.IsNullOrWhiteSpace(testCaseFilter))
        {
            return [];
        }

        var arguments = new List<string>();
        foreach (var clause in testCaseFilter.Split('&', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = clause.Split("!=", 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0] != "Category" || parts[1].Length == 0)
            {
                throw new NotSupportedException(
                    $"The MTP runner can only translate 'Category!=value' test-case-filter clauses; '{clause}' has no exact equivalent.");
            }

            arguments.Add("--filter-not-trait");
            arguments.Add($"Category={parts[1]}");
        }

        return arguments.ToArray();
    }

    public (ITestServerListener Listener, int Port) CreateListener()
    {
        var tcpListener = new TcpListener(new IPEndPoint(IPAddress.Loopback, 0));
        tcpListener.Start();
        var port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
        return (new TcpTestServerListener(tcpListener), port);
    }

    public ITestServerProcess StartProcess(string assembly, int port, Dictionary<string, string?> environmentVariables)
    {
        Stream outputStream;
        PipeTarget outputPipe;

        if (_logToFile && !string.IsNullOrEmpty(_outputPath))
        {
            var logsDirectory = Path.Combine(_outputPath, "logs", "test-servers");
            Directory.CreateDirectory(logsDirectory);

            var logFileName = $"test-server-{Path.GetFileNameWithoutExtension(assembly)}-{port}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.log";
            var logFilePath = Path.Combine(logsDirectory, logFileName);

            outputStream = File.Open(logFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            outputPipe = PipeTarget.ToStream(outputStream);
        }
        else
        {
            // Output is never consumed, so just discard it to avoid memory usage
            outputStream = Stream.Null;
            outputPipe = PipeTarget.Null;
        }

        var cliProcess = Cli.Wrap("dotnet")
            .WithWorkingDirectory(Path.GetDirectoryName(assembly) ?? string.Empty)
            .WithArguments([assembly, "--server", "--client-port", port.ToString(), .. _filterArguments])
            .WithEnvironmentVariables(environmentVariables)
            .WithStandardOutputPipe(outputPipe)
            .WithStandardErrorPipe(outputPipe)
            .ExecuteAsync();

        return new CliTestServerProcess(cliProcess, outputStream);
    }

    public ITestingPlatformClient CreateClient(Stream stream, IProcessHandle processHandle, ILogger logger, string? rpcLogFilePath)
    {
        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream, stream, new SystemTextJsonFormatter
        {
            JsonSerializerOptions = RpcJsonSerializerOptions.Default
        }));

        var tcpClient = new TcpClient();
        return new TestingPlatformClient(rpc, tcpClient, processHandle, logger, rpcLogFilePath);
    }

    private sealed class TcpTestServerListener(TcpListener listener) : ITestServerListener
    {
        public async Task<(Stream Stream, IDisposable Connection)> AcceptConnectionAsync(CancellationToken cancellationToken)
        {
            var tcpClient = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            return (tcpClient.GetStream(), tcpClient);
        }

        public void Stop() => listener.Stop();

        public void Dispose() => listener.Stop();
    }

    private sealed class CliTestServerProcess(CommandTask<CommandResult> commandTask, Stream outputStream) : ITestServerProcess
    {
        private readonly ProcessHandle _processHandle = new(commandTask, outputStream);

        public Task WaitForExitAsync() => commandTask.Task;
        public bool HasExited => commandTask.Task.IsCompleted;
        public IProcessHandle ProcessHandle => _processHandle;

        public void Dispose()
        {
            _processHandle.Dispose();
            outputStream.Dispose();
        }
    }
}
