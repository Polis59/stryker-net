#pragma warning disable CS8600, CS8601, CS8602, CS8603, CS8618, CS8625

namespace Stryker
{
    /// <summary>
    /// A static class used for controlling mutant activation and coverage tracking at runtime.
    /// It supports both environment variable-based control (for VSTest runner) and file-based control (for MTP runner with process reuse).
    /// It should only use C# features up to v2 to ensure compatibility with the widest range of projects it is injected into.
    /// </summary>
    public static class MutantControl
    {
        // Stryker mutates several assemblies per run and injects a copy of this class into each,
        // but they all execute in one test host. Coverage therefore accumulates in one
        // process-wide sink shared through AppDomain data: with a private sink per copy, each
        // copy would overwrite the shared coverage file with only its own assembly's coverage,
        // and whichever copy flushed last would win, silently dropping the rest. The sink is an
        // object[3]: covered-mutant set, covered-static-mutant set, and the lock guarding both.
        // The sets are cleared in place, never reassigned, because every copy holds the same
        // references. Interned strings give a process-global identity to synchronize creation on.
        // Initialized to an empty sentinel (never null, for nullable-enabled consumers); the real
        // three-element sink replaces it on first use.
        private static object[] _sharedCoverageSink = new object[0];
        private static string envName = string.Empty;
        // Initialized to avoid nullable warnings/errors
        private static string _cachedMutantFilePath = string.Empty;
        private static bool _mutantFilePathCached;
        private static bool _fileMutantValueCached;

        // Memory-mapped view of the mutant-id file used by the MTP runner. The runner writes the active
        // mutant id (a 4-byte int) to the file between runs; reading it through a memory-mapped view is a
        // plain memory access (no syscall). A cooperating test host refreshes ActiveMutant once before
        // each run request, allowing IsActive to avoid even that accessor read at every mutation point.
        // Hosts without the refresh hook retain the dynamic read on every call so activation cannot go stale.
        // _mutantMmf / _mutantAccessor are typed as object and initialized to a non-null sentinel only to
        // root them (and avoid nullable warnings); the accessor is cast back to its real type when read.
        private static object _mutantMmf = new System.Object();
        private static object _mutantAccessor = new System.Object();
        private static bool _mutantMmfReady;
        private static bool _mutantMmfFailed;

        // Coverage file path for MTP runner (file-based IPC)
        private static string _cachedCoverageFilePath = string.Empty;
        private static bool _coverageFilePathCached;
        private static bool _processExitRegistered;

        // Coverage flush control file for the MTP runner's per-test coverage capture. The file holds
        // two 4-byte ints: a request sequence number at offset 0 (written by the runner) and an
        // acknowledge sequence number at offset 4 (written by this process). After each single-test
        // run the runner bumps the request number; a background watcher thread in this process
        // flushes the coverage accumulated since the previous flush to the coverage file, then
        // echoes the request number into the acknowledge slot so the runner knows the file is
        // complete. This is what turns the exit-time coverage flush into a per-test protocol
        // without restarting the test host between tests.
        private static string _coverageControlFilePath = string.Empty;
        // Roots the control-file mapping (typed as object for the C#2 constraint, like _mutantMmf):
        // if the MemoryMappedFile were collected its finalizer would release the mapping handle out
        // from under the accessor the watcher thread is still reading.
        private static object _coverageControlMmf = new System.Object();

        // this attribute will be set by the Stryker Data Collector before each test
        public static bool CaptureCoverage;
        public static int ActiveMutant = -2;
        public const int ActiveMutantNotInitValue = -2;

        // --- Parallel multiplexed activation, self-contained ---
        // During a parallel multiplexed session the runner publishes a test-to-mutant map
        // under an "active-parallel" header and holds the control file at -1. Each test
        // resolves its own assigned mutant by asking xUnit v3's ambient TestContext which
        // test is executing (via cached reflection, once per test), then memoizes the
        // binding in an AsyncLocal that flows with the test's execution context - so the
        // per-mutation-point cost is a single AsyncLocal read. Work on threads that
        // predate the test carries no context and no binding, and observes no mutant.
        // No test-framework hook is required; the acknowledgement file is written here.
        private static readonly object _parallelSync = new System.Object();
        private static bool _parallelPathsProbed;
        private static string _parallelMapFile = string.Empty;
        private static string _parallelAckFile = string.Empty;
        private static string _parallelErrorFile = string.Empty;
        private static string _parallelHeader = string.Empty;
        private static System.Collections.Generic.Dictionary<string, int> _parallelAssignments;
        private static System.Threading.AsyncLocal<object[]> _parallelMemo;
        private static bool _testContextProbed;
        private static bool _testContextUnavailable;
        // Negative-probe cache: when the map header is not active-parallel, IsActive must
        // not pay a file open per mutation point. The runner always writes the map before
        // starting a run request, and the RPC round trip alone exceeds this window, so a
        // stale negative can never leak into a parallel session's test execution.
        private static long _parallelNegativeUntilTicks;
        private static System.Reflection.PropertyInfo _testContextCurrent;
        private static System.Reflection.PropertyInfo _testContextTestCase;
        private static System.Reflection.PropertyInfo _testCaseUniqueId;
        private static System.Reflection.PropertyInfo _testContextTest;
        private static System.Reflection.PropertyInfo _testDisplayName;

        private const string ParallelHeaderPrefix = "stryker-mtp-activation-map-v1\tactive-parallel\t";
        private const int ParallelUnbound = -2147483647;

        static MutantControl()
        {
            // Check for MTP file-based coverage mode at class initialization
            // Environment variable contains only the filename, not the full path
            string coverageFileName = System.Environment.GetEnvironmentVariable("STRYKER_COVERAGE_FILE") ?? string.Empty;

            if (!string.IsNullOrEmpty(coverageFileName))
            {
                // Construct full path using temp directory
                _cachedCoverageFilePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), coverageFileName);
                _coverageFilePathCached = true;
                CaptureCoverage = true;

                // Exactly one of the injected copies may write the coverage file (see the shared
                // sink comment above); the first copy whose static constructor runs wins the
                // election and owns both the exit-time flush and the flush-request watcher. All
                // copies still register coverage into the shared sink, so the elected writer
                // flushes everything.
                // Lifecycle coverage (STRYKER_COVERAGE_MAP_FILE) drains the counters after every
                // test instead, and a process-exit callback would pin a collectible test load
                // context for the lifetime of the isolation broker, so neither the exit flush nor
                // the watcher is registered while that protocol is active.
                string coverageMapFile = System.Environment.GetEnvironmentVariable("STRYKER_COVERAGE_MAP_FILE") ?? string.Empty;
                if (string.IsNullOrEmpty(coverageMapFile) && TryElectCoverageWriter())
                {
                    if (!_processExitRegistered)
                    {
                        System.AppDomain.CurrentDomain.ProcessExit += delegate { FlushCoverageToFile(); };
                        _processExitRegistered = true;
                    }

                    StartCoverageControlWatcher();
                }
            }
        }

        private static object[] GetSharedCoverageSink()
        {
            object[] cached = _sharedCoverageSink;
            if (cached.Length == 3)
            {
                return cached;
            }

            lock (string.Intern("Stryker.MutantControl.CoverageSink.Lock.v1"))
            {
                object existing = System.AppDomain.CurrentDomain.GetData("Stryker.MutantControl.CoverageSink.v1") ?? new System.Object();
                object[] sink;
                if (existing is object[] && ((object[])existing).Length == 3)
                {
                    sink = (object[])existing;
                }
                else
                {
                    sink = new object[]
                    {
                        new System.Collections.Generic.HashSet<int>(),
                        new System.Collections.Generic.HashSet<int>(),
                        new System.Object()
                    };
                    System.AppDomain.CurrentDomain.SetData("Stryker.MutantControl.CoverageSink.v1", sink);
                }

                _sharedCoverageSink = sink;
                return sink;
            }
        }

        private static bool TryElectCoverageWriter()
        {
            lock (string.Intern("Stryker.MutantControl.CoverageSink.Lock.v1"))
            {
                if (System.AppDomain.CurrentDomain.GetData("Stryker.MutantControl.CoverageWriter.v1") != null)
                {
                    return false;
                }

                System.AppDomain.CurrentDomain.SetData("Stryker.MutantControl.CoverageWriter.v1", "elected");
                return true;
            }
        }

        private static void StartCoverageControlWatcher()
        {
            // Environment variable contains only the filename; the runner puts the file in the temp
            // directory, next to the coverage file. Absent variable means the runner is capturing
            // aggregate coverage via the exit-time flush only, so no watcher is needed.
            string controlFileName = System.Environment.GetEnvironmentVariable("STRYKER_COVERAGE_CONTROL_FILE") ?? string.Empty;
            if (string.IsNullOrEmpty(controlFileName))
            {
                return;
            }

            _coverageControlFilePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), controlFileName);

            System.Threading.Thread watcher = new System.Threading.Thread(new System.Threading.ThreadStart(CoverageControlLoop));
            watcher.IsBackground = true;
            watcher.Name = "StrykerCoverageControlWatcher";
            watcher.Start();
        }

        private static void CoverageControlLoop()
        {
            // Typed as object with a non-null sentinel (like _mutantAccessor above) so the injected
            // source stays warning-free under nullable analysis; _mapped tells the two states apart.
            object accessorHolder = new System.Object();
            bool mapped = false;
            int lastHandled = 0;

            while (true)
            {
                try
                {
                    if (!mapped)
                    {
                        if (!System.IO.File.Exists(_coverageControlFilePath))
                        {
                            System.Threading.Thread.Sleep(50);
                            continue;
                        }

                        // FileShare.ReadWrite lets the runner keep writing request numbers while this
                        // process keeps the file mapped; leaveOpen: false ties the stream's lifetime
                        // to the mapping.
                        System.IO.FileStream stream = new System.IO.FileStream(
                            _coverageControlFilePath,
                            System.IO.FileMode.Open,
                            System.IO.FileAccess.ReadWrite,
                            System.IO.FileShare.ReadWrite);

                        System.IO.MemoryMappedFiles.MemoryMappedFile mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(
                            stream,
                            null,
                            8,
                            System.IO.MemoryMappedFiles.MemoryMappedFileAccess.ReadWrite,
                            System.IO.HandleInheritability.None,
                            false);

                        System.IO.MemoryMappedFiles.MemoryMappedViewAccessor createdAccessor =
                            mmf.CreateViewAccessor(0, 8, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.ReadWrite);

                        _coverageControlMmf = mmf;
                        accessorHolder = createdAccessor;
                        mapped = true;

                        // Resume from the acknowledge slot rather than zero so a watcher restarted
                        // after a transient mapping failure does not re-acknowledge (and re-reset)
                        // coverage for a request that was already served.
                        lastHandled = createdAccessor.ReadInt32(4);
                    }

                    System.IO.MemoryMappedFiles.MemoryMappedViewAccessor accessor =
                        (System.IO.MemoryMappedFiles.MemoryMappedViewAccessor)accessorHolder;
                    int requested = accessor.ReadInt32(0);
                    if (requested != lastHandled)
                    {
                        // Write the coverage accumulated since the previous flush, then acknowledge.
                        // Ordering matters: the runner only reads the coverage file after seeing the
                        // acknowledge slot match its request number.
                        FlushCoverageToFile();
                        accessor.Write(4, requested);
                        accessor.Flush();
                        lastHandled = requested;
                    }
                }
                catch (System.Exception)
                {
                    // The mapping became unusable (e.g. the runner deleted the file between runs);
                    // drop it and retry, so a fresh control file re-establishes the protocol.
                    if (mapped)
                    {
                        try
                        {
                            ((System.IDisposable)accessorHolder).Dispose();
                        }
                        catch (System.Exception)
                        {
                            // Nothing further to release.
                        }
                        accessorHolder = new System.Object();
                        mapped = false;
                    }
                    System.Threading.Thread.Sleep(50);
                }

                System.Threading.Thread.Sleep(1);
            }
        }

        public static void InitCoverage()
        {
            ResetCoverage();
        }

        public static void ResetCoverage()
        {
            object[] sink = GetSharedCoverageSink();
            lock (sink[2])
            {
                // Clear in place: every injected copy holds references to these same lists.
                ((System.Collections.Generic.HashSet<int>)sink[0]).Clear();
                ((System.Collections.Generic.HashSet<int>)sink[1]).Clear();
            }
        }

        public static void ResetActiveMutant()
        {
            ActiveMutant = ActiveMutantNotInitValue;
            _fileMutantValueCached = false;
        }

        /// <summary>
        /// Refreshes the whole-session mutant selected by the MTP runner. Test-framework hooks call this
        /// once before each run request so mutation points can compare against a process-local integer.
        /// </summary>
        public static void RefreshActiveMutantFromFile()
        {
            int fileMutantId;
            if (TryReadMutantFromFile(out fileMutantId))
            {
                ActiveMutant = fileMutantId;
                _fileMutantValueCached = true;
            }
            else
            {
                _fileMutantValueCached = false;
            }
        }

        /// <summary>
        /// <summary>
        /// Resolves the mutant bound to the currently executing test during a parallel
        /// multiplexed session. Returns false when no parallel session is active (callers
        /// fall through to the control-file path). When a parallel session is active the
        /// out value is the bound mutant id, or <see cref="ParallelUnbound"/> for execution
        /// that carries no test binding (background threads, unmapped runtime rows after
        /// the error file is recorded) - such execution must observe no mutant.
        /// </summary>
        private static bool TryGetParallelMutant(out int mutantId)
        {
            mutantId = ParallelUnbound;

            if (!_parallelPathsProbed)
            {
                lock (_parallelSync)
                {
                    if (!_parallelPathsProbed)
                    {
                        _parallelMapFile = System.Environment.GetEnvironmentVariable("STRYKER_MUTANT_MAP_FILE") ?? string.Empty;
                        _parallelAckFile = System.Environment.GetEnvironmentVariable("STRYKER_MUTANT_MAP_ACK_FILE") ?? string.Empty;
                        _parallelErrorFile = System.Environment.GetEnvironmentVariable("STRYKER_MUTANT_MAP_ERROR_FILE") ?? string.Empty;
                        _parallelMemo = new System.Threading.AsyncLocal<object[]>();
                        _parallelPathsProbed = true;
                    }
                }
            }

            if (_parallelMapFile.Length == 0)
            {
                return false;
            }

            // Fast path: a binding memoized earlier in this execution context. The memo was
            // created inside the current test's async flow, so it cannot leak across tests.
            object[] memo = _parallelMemo.Value;
            if (memo != null)
            {
                string memoHeader = (string)memo[0];
                if (string.Equals(memoHeader, _parallelHeader, System.StringComparison.Ordinal))
                {
                    mutantId = (int)memo[1];
                    return true;
                }
            }

            if (System.Environment.TickCount64 < System.Threading.Interlocked.Read(ref _parallelNegativeUntilTicks))
            {
                return false;
            }

            return ResolveParallelBinding(out mutantId);
        }

        private static bool ResolveParallelBinding(out int mutantId)
        {
            mutantId = ParallelUnbound;

            string header;
            try
            {
                using (System.IO.StreamReader reader = System.IO.File.OpenText(_parallelMapFile))
                {
                    header = reader.ReadLine() ?? string.Empty;
                    if (!header.StartsWith(ParallelHeaderPrefix, System.StringComparison.Ordinal))
                    {
                        System.Threading.Interlocked.Exchange(
                            ref _parallelNegativeUntilTicks,
                            System.Environment.TickCount64 + 20);
                        return false;
                    }

                    lock (_parallelSync)
                    {
                        if (!string.Equals(_parallelHeader, header, System.StringComparison.Ordinal) ||
                            _parallelAssignments == null)
                        {
                            System.Collections.Generic.Dictionary<string, int> assignments =
                                new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.Ordinal);
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                int separator = line.IndexOf('\t');
                                if (separator <= 0)
                                {
                                    continue;
                                }
                                int assignedMutant;
                                if (int.TryParse(line.Substring(0, separator), out assignedMutant))
                                {
                                    assignments[line.Substring(separator + 1)] = assignedMutant;
                                }
                            }

                            _parallelAssignments = assignments;
                            _parallelHeader = header;
                        }
                    }
                }
            }
            catch (System.Exception)
            {
                // The runner rewrites the map between requests; a transient read failure is
                // retried after the negative-probe window rather than on every mutation point.
                System.Threading.Interlocked.Exchange(
                    ref _parallelNegativeUntilTicks,
                    System.Environment.TickCount64 + 20);
                return false;
            }

            string testCaseUid;
            string displayName;
            if (!TryReadCurrentTest(out testCaseUid, out displayName))
            {
                if (_testContextUnavailable)
                {
                    RecordParallelError("The xUnit v3 TestContext is unavailable; parallel multiplexed activation cannot bind tests.");
                    // Bound-but-unresolvable: the error file invalidates the session; local
                    // execution observes no mutant.
                    _parallelMemo.Value = new object[] { _parallelHeader, ParallelUnbound };
                    return true;
                }

                // Execution outside any test (background threads): no binding, no mutant.
                _parallelMemo.Value = new object[] { _parallelHeader, ParallelUnbound };
                return true;
            }

            System.Collections.Generic.Dictionary<string, int> current = _parallelAssignments;
            int resolved;
            if (!current.TryGetValue(testCaseUid, out resolved))
            {
                string methodKey = MethodAssignmentKey(displayName);
                if (methodKey == null || !current.TryGetValue(methodKey, out resolved))
                {
                    RecordParallelError("Test case '" + testCaseUid + "' has no mutant assignment in the active MTP request.");
                    _parallelMemo.Value = new object[] { _parallelHeader, ParallelUnbound };
                    return true;
                }
            }

            AcknowledgeParallelMap(_parallelHeader);
            _parallelMemo.Value = new object[] { _parallelHeader, resolved };
            mutantId = resolved;
            return true;
        }

        private static string MethodAssignmentKey(string displayName)
        {
            if (string.IsNullOrEmpty(displayName) ||
                displayName.IndexOf('\r') >= 0 ||
                displayName.IndexOf('\n') >= 0 ||
                displayName.IndexOf('\t') >= 0)
            {
                return null;
            }

            int argumentsStart = displayName.IndexOf('(');
            string methodDisplay = argumentsStart < 0 ? displayName : displayName.Substring(0, argumentsStart);
            return methodDisplay.Length == 0 ? null : "method\t" + methodDisplay;
        }

        private static bool TryReadCurrentTest(out string testCaseUid, out string displayName)
        {
            testCaseUid = null;
            displayName = null;

            if (!_testContextProbed)
            {
                lock (_parallelSync)
                {
                    if (!_testContextProbed)
                    {
                        try
                        {
                            System.Type contextType = null;
                            foreach (System.Reflection.Assembly assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                            {
                                if (assembly.GetName().Name == "xunit.v3.core")
                                {
                                    contextType = assembly.GetType("Xunit.TestContext", false);
                                    break;
                                }
                            }

                            if (contextType != null)
                            {
                                _testContextCurrent = contextType.GetProperty("Current",
                                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                                _testContextTestCase = contextType.GetProperty("TestCase");
                                _testContextTest = contextType.GetProperty("Test");
                            }

                            _testContextUnavailable = _testContextCurrent == null;
                        }
                        catch (System.Exception)
                        {
                            _testContextUnavailable = true;
                        }

                        _testContextProbed = true;
                    }
                }
            }

            if (_testContextUnavailable)
            {
                return false;
            }

            try
            {
                object context = _testContextCurrent.GetValue(null, null);
                if (context == null)
                {
                    return false;
                }

                object testCase = _testContextTestCase != null ? _testContextTestCase.GetValue(context, null) : null;
                if (testCase == null)
                {
                    return false;
                }

                if (_testCaseUniqueId == null)
                {
                    _testCaseUniqueId = testCase.GetType().GetProperty("UniqueID");
                }
                object uid = _testCaseUniqueId != null ? _testCaseUniqueId.GetValue(testCase, null) : null;
                if (uid == null)
                {
                    return false;
                }
                testCaseUid = (string)uid;

                object test = _testContextTest != null ? _testContextTest.GetValue(context, null) : null;
                if (test != null)
                {
                    if (_testDisplayName == null)
                    {
                        _testDisplayName = test.GetType().GetProperty("TestDisplayName");
                    }
                    if (_testDisplayName != null)
                    {
                        displayName = _testDisplayName.GetValue(test, null) as string;
                    }
                }

                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        private static void AcknowledgeParallelMap(string header)
        {
            if (_parallelAckFile.Length == 0)
            {
                return;
            }

            string acknowledgement = header.Substring(ParallelHeaderPrefix.Length);
            try
            {
                if (!System.IO.File.Exists(_parallelAckFile) ||
                    System.IO.File.ReadAllText(_parallelAckFile) != acknowledgement)
                {
                    System.IO.File.WriteAllText(_parallelAckFile, acknowledgement);
                }
            }
            catch (System.Exception)
            {
                // A concurrent writer produced the same token; the runner only compares content.
            }
        }

        private static void RecordParallelError(string message)
        {
            if (_parallelErrorFile.Length == 0)
            {
                return;
            }

            try
            {
                if (!System.IO.File.Exists(_parallelErrorFile))
                {
                    System.IO.File.WriteAllText(_parallelErrorFile, message);
                }
            }
            catch (System.Exception)
            {
                // The session is already being invalidated by another writer.
            }
        }

        public static void SetActiveMutantViaEnvironmentVariable(int mutantId)
        {
            // Ensure we never assign null to a non-nullable string
            string environmentVariableName = System.Environment.GetEnvironmentVariable("STRYKER_MUTANT_ID_CONTROL_VAR") ?? string.Empty;
            if (environmentVariableName.Length > 0)
            {
                System.Environment.SetEnvironmentVariable(environmentVariableName, mutantId.ToString());
            }
            ActiveMutant = ActiveMutantNotInitValue;
            _fileMutantValueCached = false;
        }

        private static bool TryReadMutantFromFile(out int mutantId)
        {
            mutantId = -1;

            // Cache the mutant file path to avoid repeated environment variable lookups
            if (!_mutantFilePathCached)
            {
                // coalesce null to empty string so _cachedMutantFilePath is never null
                _cachedMutantFilePath = System.Environment.GetEnvironmentVariable("STRYKER_MUTANT_FILE") ?? string.Empty;
                _mutantFilePathCached = true;
            }

            if (string.IsNullOrEmpty(_cachedMutantFilePath))
            {
                return false;
            }

            // Fast path: read the active mutant id from a memory-mapped view of the file (see the field
            // comments above). This is a plain memory read - no filesystem stat or content read per call.
            if (!_mutantMmfFailed)
            {
                if (!_mutantMmfReady)
                {
                    EnsureMutantMmf();
                }

                if (_mutantMmfReady)
                {
                    try
                    {
                        mutantId = ((System.IO.MemoryMappedFiles.MemoryMappedViewAccessor)_mutantAccessor).ReadInt32(0);
                        return true;
                    }
                    catch
                    {
                        // The mapping became unusable; fall back to reading the file directly from now on.
                        _mutantMmfFailed = true;
                    }
                }
            }

            // Fallback (no memory-mapped view could be created): read the 4-byte mutant id straight from
            // the file on every call. Correct but slower; only used if memory mapping is unavailable.
            return TryReadMutantFromFileDirect(out mutantId);
        }

        private static void EnsureMutantMmf()
        {
            if (_mutantMmfReady || _mutantMmfFailed)
            {
                return;
            }

            // The runner creates the file (with the initial -1) before the test host starts, so it
            // normally exists on the first call. If it is not there yet, leave things unmapped and retry
            // on a later call rather than giving up permanently.
            if (!System.IO.File.Exists(_cachedMutantFilePath))
            {
                return;
            }

            try
            {
                // FileShare.ReadWrite lets the runner keep writing the file while the test host keeps it mapped.
                // leaveOpen: false means the mapping owns and disposes the stream with it.
                System.IO.FileStream stream = new System.IO.FileStream(
                    _cachedMutantFilePath,
                    System.IO.FileMode.Open,
                    System.IO.FileAccess.Read,
                    System.IO.FileShare.ReadWrite);

                System.IO.MemoryMappedFiles.MemoryMappedFile mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(
                    stream,
                    null,
                    4,
                    System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read,
                    System.IO.HandleInheritability.None,
                    false);

                System.IO.MemoryMappedFiles.MemoryMappedViewAccessor accessor = mmf.CreateViewAccessor(0, 4, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);

                _mutantMmf = mmf;
                _mutantAccessor = accessor;
                _mutantMmfReady = true;
            }
            catch
            {
                // Memory mapping is unavailable in this environment; the direct-read fallback keeps the active mutant correct (just slower).
                _mutantMmfFailed = true;
            }
        }

        private static bool TryReadMutantFromFileDirect(out int mutantId)
        {
            mutantId = -1;

            if (!System.IO.File.Exists(_cachedMutantFilePath))
            {
                return false;
            }

            try
            {
                byte[] bytes = System.IO.File.ReadAllBytes(_cachedMutantFilePath);
                if (bytes.Length >= 4)
                {
                    mutantId = System.BitConverter.ToInt32(bytes, 0);
                    return true;
                }
            }
            catch
            {
                // Ignore file read errors
            }
            return false;
        }

        public static System.Collections.Generic.IList<int>[] GetCoverageData()
        {
            object[] sink = GetSharedCoverageSink();
            System.Collections.Generic.List<int> covered;
            System.Collections.Generic.List<int> coveredStatic;
            lock (sink[2])
            {
                covered = new System.Collections.Generic.List<int>((System.Collections.Generic.HashSet<int>)sink[0]);
                coveredStatic = new System.Collections.Generic.List<int>((System.Collections.Generic.HashSet<int>)sink[1]);
                ((System.Collections.Generic.HashSet<int>)sink[0]).Clear();
                ((System.Collections.Generic.HashSet<int>)sink[1]).Clear();
            }

            return new System.Collections.Generic.IList<int>[] { covered, coveredStatic };
        }

        /// <summary>
        /// Writes accumulated coverage data to a file for MTP runner IPC.
        /// Called automatically on process exit to capture all coverage from tests run in this process.
        /// Format: "coveredMutants;staticMutants" (comma-separated IDs)
        /// </summary>
        public static void FlushCoverageToFile()
        {
            if (!_coverageFilePathCached)
            {
                // Environment variable contains only the filename
                string coverageFileName = System.Environment.GetEnvironmentVariable("STRYKER_COVERAGE_FILE") ?? string.Empty;
                if (!string.IsNullOrEmpty(coverageFileName))
                {
                    // Construct full path using temp directory
                    _cachedCoverageFilePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), coverageFileName);
                }
                _coverageFilePathCached = true;
            }

            if (string.IsNullOrEmpty(_cachedCoverageFilePath))
            {
                return;
            }

            try
            {
                object[] sink = GetSharedCoverageSink();
                lock (sink[2])
                {
                    string covered = string.Join(",", (System.Collections.Generic.HashSet<int>)sink[0]);
                    string staticMutants = string.Join(",", (System.Collections.Generic.HashSet<int>)sink[1]);
                    string content = covered + ";" + staticMutants;
                    System.IO.File.WriteAllText(_cachedCoverageFilePath, content);
                    ((System.Collections.Generic.HashSet<int>)sink[0]).Clear();
                    ((System.Collections.Generic.HashSet<int>)sink[1]).Clear();
                }
            }
            catch (System.Exception ex)
            {
                // Do not fail tests due to coverage write issues; log for diagnostics instead.
                System.Diagnostics.Debug.WriteLine(string.Format("[Stryker] Failed to flush coverage to file '{0}': {1}", _cachedCoverageFilePath, ex));
            }
        }

        // check with: Stryker.MutantControl.IsActive(ID)
        public static bool IsActive(int id)
        {
            if (CaptureCoverage)
            {
                RegisterCoverage(id);
                return false;
            }

            // Parallel multiplexed sessions take precedence: each test resolves its own
            // assigned mutant through xUnit's ambient TestContext, memoized per execution
            // flow. Unbound execution (background threads, unmapped rows) observes no mutant.
            int parallelMutant;
            if (TryGetParallelMutant(out parallelMutant))
            {
                return id == parallelMutant;
            }

            // File-based mutant control (used by the MTP runner's persistent test hosts).
            // A cooperating host refreshes the value once at request start. Without that hook,
            // retain the per-call read so older integrations continue to observe runner updates.
            if (!_mutantFilePathCached || !string.IsNullOrEmpty(_cachedMutantFilePath))
            {
                if (!_fileMutantValueCached)
                {
                    int fileMutantId;
                    if (TryReadMutantFromFile(out fileMutantId))
                    {
                        return id == fileMutantId;
                    }
                }
                else
                {
                    return id == ActiveMutant;
                }
            }

            // lazy load the active mutant id from the environment variable (used by VSTest runner)
            if (ActiveMutant == ActiveMutantNotInitValue)
            {
                // coalesce null to empty string to avoid null-to-non-nullable conversion
                string environmentVariableName = System.Environment.GetEnvironmentVariable("STRYKER_MUTANT_ID_CONTROL_VAR") ?? string.Empty;
                if (environmentVariableName.Length > 0)
                {
                    string environmentVariable = System.Environment.GetEnvironmentVariable(environmentVariableName) ?? string.Empty;
                    if (string.IsNullOrEmpty(environmentVariable))
                    {
                        ActiveMutant = -1;
                    }
                    else
                    {
                        ActiveMutant = int.Parse(environmentVariable);
                    }
                }
                else
                {
                    ActiveMutant = -1;
                }
            }

            return id == ActiveMutant;
        }

        private static void RegisterCoverage(int id)
        {
            object[] sink = GetSharedCoverageSink();
            lock (sink[2])
            {
                System.Collections.Generic.HashSet<int> covered = (System.Collections.Generic.HashSet<int>)sink[0];
                covered.Add(id);
                if (MutantContext.InStatic())
                {
                    System.Collections.Generic.HashSet<int> coveredStatic = (System.Collections.Generic.HashSet<int>)sink[1];
                    coveredStatic.Add(id);
                }
            }
        }
    }
}
