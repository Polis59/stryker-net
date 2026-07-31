using System.IO.Pipes;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;

namespace Stryker.TestIsolationHost;

internal static class Program
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        if (args is not ["--pipe", var pipeName] ||
            string.IsNullOrWhiteSpace(pipeName))
        {
            Console.Error.WriteLine("Usage: Stryker.TestIsolationHost --pipe <name>");
            return 2;
        }

        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await pipe.WaitForConnectionAsync().ConfigureAwait(false);

        using var reader = new StreamReader(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            leaveOpen: true)
        {
            AutoFlush = true,
        };

        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            IsolationRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<IsolationRequest>(
                    line,
                    SerializerOptions);
            }
            catch (JsonException exception)
            {
                await WriteResponse(
                    writer,
                    IsolationResponse.RuntimeError(
                        $"The isolation request was invalid: {exception.Message}"))
                    .ConfigureAwait(false);
                continue;
            }

            if (request is null)
            {
                await WriteResponse(
                    writer,
                    IsolationResponse.RuntimeError("The isolation request was empty."))
                    .ConfigureAwait(false);
                continue;
            }

            if (request.Shutdown)
            {
                return 0;
            }

            await WriteResponse(
                writer,
                await ExecuteInCollectibleContext(request).ConfigureAwait(false))
                .ConfigureAwait(false);
        }

        return 0;
    }

    private static Task WriteResponse(
        StreamWriter writer,
        IsolationResponse response) =>
        writer.WriteLineAsync(JsonSerializer.Serialize(response, SerializerOptions));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<IsolationResponse> ExecuteInCollectibleContext(
        IsolationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AssemblyPath) ||
            !File.Exists(request.AssemblyPath))
        {
            return IsolationResponse.RuntimeError(
                $"The test assembly '{request.AssemblyPath}' does not exist.");
        }

        WeakReference weakReference;
        IsolationResponse response;
        try
        {
            (weakReference, response) = InvokeOnDedicatedThread(request);
        }
        catch (Exception exception)
        {
            return IsolationResponse.RuntimeError(
                exception.GetBaseException().ToString());
        }

        try
        {
            ClearJsonReflectionCaches();
        }
        catch (Exception exception)
        {
            response = IsolationResponse.RuntimeError(
                exception.GetBaseException().ToString());
        }

        // One collection pass reclaims the common case promptly; the context's eventual death is
        // not awaited or proven. Every request builds a brand-new collectible context, so a
        // predecessor that lingers until a later GC cannot leak state into the next mutant's
        // verdict — blocking here traded up to eight forced full GC cycles per request (minutes
        // per campaign across a thousand confirmations) for a memory guarantee the verdicts
        // never needed.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        _ = weakReference;
        await Task.Yield();

        return response with { Unloaded = true };
    }

    private static void ClearJsonReflectionCaches()
    {
        // Collectible contexts are expected to become unreachable after `Unload`,
        // but System.Text.Json's process-wide reflection accessors retain dynamic
        // delegates for one second. Invoke the runtime's own hot-reload cache
        // invalidator so test DTO types do not pin the context across requests.
        var updateHandler = typeof(JsonSerializerOptions).Assembly.GetType(
            "System.Text.Json.JsonSerializerOptionsUpdateHandler",
            throwOnError: true)!;
        var clearCache = updateHandler.GetMethod(
            "ClearCache",
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(
                updateHandler.FullName,
                "ClearCache");
        clearCache.Invoke(null, [Array.Empty<Type>()]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Context, IsolationResponse Response)
        InvokeOnDedicatedThread(IsolationRequest request)
    {
        (WeakReference Context, IsolationResponse Response)? result = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = InvokeAndUnload(request);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
            Name = "Stryker collectible test executor",
        };
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result
            ?? throw new InvalidOperationException(
                "The collectible test executor thread returned no result.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Context, IsolationResponse Response)
        InvokeAndUnload(IsolationRequest request)
    {
        var loadContext = new TestAssemblyLoadContext(
            request.AssemblyPath!,
            typeof(Program).Assembly.Location);
        var weakReference = new WeakReference(loadContext, trackResurrection: false);
        try
        {
            var hostAssembly = loadContext.LoadFromAssemblyPath(
                typeof(Program).Assembly.Location);
            var executorType = hostAssembly.GetType(
                "Stryker.TestIsolationHost.CollectibleTestExecutor",
                throwOnError: true)!;
            var execute = executorType.GetMethod(
                "Execute",
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException(
                    executorType.FullName,
                    "Execute");
            var responsePayload = (string?)execute.Invoke(
                null,
                [
                    request.AssemblyPath,
                    string.Join('\n', request.TestCaseIds),
                    request.Parallel ? "parallel" : "serial",
                ])
                ?? throw new InvalidDataException(
                    "The collectible test executor returned no response.");
            var response = ParseExecutorResponse(responsePayload);
            return (weakReference, response);
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private static IsolationResponse ParseExecutorResponse(string payload)
    {
        var lines = payload.Split('\n');
        if (lines.Length < 3 ||
            !long.TryParse(
                lines[0],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var durationTicks) ||
            !int.TryParse(
                lines[2],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var testCount) ||
            lines.Length != testCount + 3)
        {
            throw new InvalidDataException(
                "The collectible test executor returned an invalid response.");
        }

        var tests = new List<IsolationTestResult>(testCount);
        for (var index = 0; index < testCount; index++)
        {
            var columns = lines[index + 3].Split('\t');
            if (columns.Length != 3)
            {
                throw new InvalidDataException(
                    "The collectible test executor returned an invalid test result.");
            }

            tests.Add(new IsolationTestResult(
                Decode(columns[0]),
                columns[1],
                columns[2].Length == 0 ? null : Decode(columns[2])));
        }

        return new IsolationResponse(
            tests,
            lines[1].Length == 0 ? null : Decode(lines[1]),
            durationTicks,
            Unloaded: false);
    }

    private static string Decode(string value) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(value));
}

internal sealed class TestAssemblyLoadContext(
    string testAssemblyPath,
    string hostAssemblyPath)
    : AssemblyLoadContext(isCollectible: true)
{
    private readonly AssemblyDependencyResolver testResolver =
        new(testAssemblyPath);
    private readonly AssemblyDependencyResolver hostResolver =
        new(hostAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var path =
            testResolver.ResolveAssemblyToPath(assemblyName) ??
            hostResolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path =
            testResolver.ResolveUnmanagedDllToPath(unmanagedDllName) ??
            hostResolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? 0 : LoadUnmanagedDllFromPath(path);
    }
}

internal sealed record IsolationRequest(
    string? AssemblyPath,
    IReadOnlyList<string> TestCaseIds,
    bool Shutdown = false,
    bool Parallel = false);

internal sealed record IsolationTestResult(
    string TestCaseId,
    string State,
    string? Message);

internal sealed record IsolationResponse(
    IReadOnlyList<IsolationTestResult> Tests,
    string? Error,
    long DurationTicks,
    bool Unloaded)
{
    internal static IsolationResponse RuntimeError(string error) =>
        new([], error, 0, Unloaded: false);
}
