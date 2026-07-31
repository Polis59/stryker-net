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
        // True once the active mutant id has been read from the control file; see IsActive.
        private static bool _fileMutantValueCached;

        // Memory-mapped view of the mutant-id file used by the MTP runner. The runner writes the active
        // mutant id (a 4-byte int) to the file between runs; reading it through a memory-mapped view is a
        // plain memory access (no syscall), so it is cheap enough for the IsActive hot path while still
        // always reflecting the latest value the runner wrote. The test host process is reused across
        // mutant runs and has no per-run reset hook, so reading every call (rather than caching) is what
        // keeps this correct: any cached or event-based scheme would race the start of the next run.
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
        }

        /// <summary>
        /// Re-reads the active mutant id from the control file and refreshes the cached value.
        /// The activation sink in the test framework calls this (via reflection, on every
        /// injected copy) at the start of each run request and after each per-test switch;
        /// between those moments IsActive compares against the cached value only.
        /// </summary>
        public static void RefreshActiveMutantFromFile()
        {
            int fileMutantId;
            if (TryReadMutantFromFile(out fileMutantId))
            {
                ActiveMutant = fileMutantId;
                _fileMutantValueCached = true;
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

            // Check for file-based mutant control (used by MTP runner for process reuse).
            // The file is read once and cached: IsActive executes at every mutation point,
            // millions of times per test, and a per-call memory-mapped read multiplies every
            // test's cost several-fold across an entire campaign. The activation sink in the
            // test framework owns invalidation instead - it calls
            // RefreshActiveMutantFromFile at the start of every run request and after every
            // per-test switch, which are the only moments the file legitimately changes for a
            // live test host. A freshly loaded copy (for example in a new collectible context)
            // reads the file on its first call and keeps that value for the context's life.
            if (!_mutantFilePathCached || !string.IsNullOrEmpty(_cachedMutantFilePath))
            {
                if (!_fileMutantValueCached)
                {
                    int fileMutantId;
                    if (TryReadMutantFromFile(out fileMutantId))
                    {
                        ActiveMutant = fileMutantId;
                        _fileMutantValueCached = true;
                    }
                }

                // If we cached the file path and it's set, always use file-based control
                if (_mutantFilePathCached && !string.IsNullOrEmpty(_cachedMutantFilePath))
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
