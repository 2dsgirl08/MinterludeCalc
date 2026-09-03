using Microsoft.Diagnostics.Runtime;
using System.Diagnostics;
using System.Text.Json;

namespace MinterludeCalc
{
    public class ChartReader
    {
        const string ProcessName = "Interlude";

        // Type names we resolve against. Kept in one place because they're also
        // used to *validate* cached addresses on every read (see below).
        const string RateCallbackTypeName = "Interlude.Features.Gameplay.SelectedChart+rate@366";
        const string SettingTriggerTypePrefix = "Percyqaz.Common.Setting+trigger@105";
        const string StringRefTypeName = "Microsoft.FSharp.Core.FSharpRef<System.String>";
        const string StringTypeName = "System.String";
        const string ChartMetaTypeName = "Prelude.Data.Library.ChartMeta";

        // Anything outside this is garbage from a stale pointer, not a real rate.
        const float MinPlausibleRate = 0.1f;
        const float MaxPlausibleRate = 10.0f;

        // Re-resolving means a full heap walk, so don't let a persistently
        // unresolvable state turn the 500ms poll loop into a heap-walk loop.
        static readonly TimeSpan ResolveCooldown = TimeSpan.FromSeconds(3);

        private DataTarget? _dataTarget;
        private ClrRuntime? _runtime;
        private Process? _process;

        // ---------------------------------------------------------------------
        // IMPORTANT: every address cached in this class is a raw heap address in
        // a *running* process whose GC compacts. A gen2/compacting collection
        // inside Interlude moves these objects and silently invalidates every
        // one of them - which is especially likely while the game sits idle in
        // the background (tabbed out), because that is exactly when it has the
        // spare time to do a full blocking collection.
        //
        // So nothing cached here is trusted blindly: each read checks that the
        // object still living at the cached address is of the type we expect,
        // and re-resolves from scratch when it isn't. Without that check a
        // single background GC permanently wedges the reader - it goes on
        // reading whatever now occupies the old address, fails, and never
        // recovers, which shows up as "the overlay froze and stopped following
        // song select".
        // ---------------------------------------------------------------------

        // ---- Rate ----
        private ulong _rateGetterAddress;
        private ulong _rateGetterXFieldOffset;
        private ulong _rateRefContentsOffset;
        // Captured from the objects themselves at resolve time. Validating
        // against a *hardcoded* guess at what these types are is how you end up
        // with a check that can never pass and a reader that never reads.
        private string _rateGetterTypeName = "";
        private string _rateRefTypeName = "";
        private DateTime _lastRateResolveAttempt = DateTime.MinValue;

        // ---- Selected chart ----
        private ulong _selectedChartRefAddress;
        private ulong _selectedChartContentsOffset;
        private DateTime _lastSelectedChartResolveAttempt = DateTime.MinValue;

        // ---- Chart library ----
        private readonly Dictionary<string, ulong> _chartAddresses = new();

        public Dictionary<string, ChartInfo> Charts { get; private set; } = new();

        /// <summary>
        /// The hash the game last reported as selected, even when it isn't in
        /// <see cref="Charts"/> yet (i.e. a chart imported after we cached the
        /// library). Lets callers tell "couldn't read the selection" apart from
        /// "read it fine, but our library snapshot is out of date".
        /// </summary>
        public string? LastSelectedHash { get; private set; }

        /// <summary>
        /// Interlude's working directory (where Songs/charts.db, Songs/.assets,
        /// etc. live). Resolved once during Attach() - see ResolveWorkingDirectory.
        /// </summary>
        public string WorkingDirectory { get; private set; } = "";

        /// <summary>True while we hold a live runtime on a still-running Interlude.</summary>
        public bool IsAttached => _runtime != null && _dataTarget != null && IsProcessAlive;

        private bool IsProcessAlive
        {
            get
            {
                if (_process == null)
                    return false;

                try
                {
                    _process.Refresh();
                    return !_process.HasExited;
                }
                catch
                {
                    return false;
                }
            }
        }

        public void Attach()
        {
            Detach();

            var process = Process.GetProcessesByName(ProcessName).FirstOrDefault();
            if (process == null)
                throw new InvalidOperationException($"Process '{ProcessName}' not found.");

            _process = process;
            _dataTarget = DataTarget.AttachToProcess(process.Id, suspend: false);

            var runtimeInfo = _dataTarget.ClrVersions.FirstOrDefault()
                ?? throw new InvalidOperationException("No CLR runtime found in target process.");

            _runtime = runtimeInfo.CreateRuntime();

            if (!_runtime.Heap.CanWalkHeap)
                throw new InvalidOperationException("Cannot walk heap.");

            WorkingDirectory = ResolveWorkingDirectory(process);

            if (!TryResolveRateAddresses(force: true))
                throw new InvalidOperationException("Could not resolve the rate setting in Interlude's memory.");
        }

        /// <summary>
        /// Drops the runtime and every cached address. Safe to call at any time;
        /// the caller is expected to <see cref="Attach"/> again afterwards (e.g.
        /// because Interlude was closed and reopened, which gives every address
        /// we hold a completely new meaning).
        /// </summary>
        public void Detach()
        {
            try { _runtime?.Dispose(); } catch { }
            try { _dataTarget?.Dispose(); } catch { }
            try { _process?.Dispose(); } catch { }

            _runtime = null;
            _dataTarget = null;
            _process = null;

            InvalidateAllCaches();

            Charts = new();
            LastSelectedHash = null;
        }

        /// <summary>
        /// Forgets every resolved address so the next read re-discovers them.
        /// Callers use this when readings look stuck but the process is still
        /// alive - much cheaper than a full re-attach.
        /// </summary>
        public void InvalidateAllCaches()
        {
            _rateGetterAddress = 0;
            _rateGetterXFieldOffset = 0;
            _rateRefContentsOffset = 0;
            _rateGetterTypeName = "";
            _rateRefTypeName = "";
            _lastRateResolveAttempt = DateTime.MinValue;

            _selectedChartRefAddress = 0;
            _selectedChartContentsOffset = 0;
            _lastSelectedChartResolveAttempt = DateTime.MinValue;

            _chartAddresses.Clear();
        }

        /// <summary>
        /// Interlude stores its data next to the executable by default, but
        /// config.json (also next to the exe) can point WorkingDirectory
        /// somewhere else entirely. We need this to find Songs/charts.db later.
        /// </summary>
        private static string ResolveWorkingDirectory(Process process)
        {
            string? exeDir;

            try
            {
                exeDir = Path.GetDirectoryName(process.MainModule?.FileName);
            }
            catch
            {
                exeDir = null;
            }

            if (string.IsNullOrEmpty(exeDir))
                throw new InvalidOperationException("Could not determine Interlude's executable directory.");

            string configPath = Path.Combine(exeDir, "config.json");

            if (File.Exists(configPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(configPath));

                    if (doc.RootElement.TryGetProperty("WorkingDirectory", out var wd))
                    {
                        string? value = wd.GetString();

                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            // WorkingDirectory in config.json is relative to the exe directory.
                            return Path.GetFullPath(Path.Combine(exeDir, value));
                        }
                    }
                }
                catch
                {
                    // Malformed/unreadable config.json - fall back to the exe directory below.
                }
            }

            return exeDir;
        }

        private void EnsureAttached()
        {
            if (_runtime == null || _dataTarget == null)
                throw new InvalidOperationException("Not attached to a process.");

            if (!IsProcessAlive)
                throw new InvalidOperationException("Interlude is no longer running.");
        }

        /// <summary>
        /// Cheap way to make sure subsequent reads see current process state,
        /// without paying the cost of tearing down and recreating the runtime.
        /// Note this also invalidates every ClrType/ClrInstanceField the runtime
        /// handed out earlier, so nothing of that kind is cached across calls -
        /// only plain field offsets, which are fixed per type.
        /// </summary>
        private void RefreshHeap()
        {
            if (_runtime == null)
                throw new InvalidOperationException("Not attached to a process.");

            _runtime.FlushCachedData();
        }

        /// <summary>Is the object at this address still the type we resolved it as?</summary>
        private bool IsObjectOfType(ulong address, string typeName, out ClrObject obj)
        {
            obj = default;

            if (address == 0 || _runtime == null || string.IsNullOrEmpty(typeName))
                return false;

            try
            {
                obj = _runtime.Heap.GetObject(address);
                return obj.IsValid && obj.Type?.Name == typeName;
            }
            catch
            {
                // A stale address can point anywhere, including at bytes ClrMD
                // refuses to interpret. That's a cache miss, not an error.
                return false;
            }
        }

        private string? ReadStringAt(ulong address)
        {
            if (!IsObjectOfType(address, StringTypeName, out var obj))
                return null;

            try
            {
                return obj.AsString();
            }
            catch
            {
                return null;
            }
        }

        // =====================================================================
        // RATE
        // =====================================================================

        /// <summary>
        /// Reads the current rate with two direct memory reads - no heap
        /// enumeration - for as long as the cached addresses still check out.
        /// When they stop checking out (see the GC note at the top of the class)
        /// it re-resolves them and tries again, so a background collection in
        /// the game costs one slow read instead of permanently breaking rate
        /// tracking.
        /// </summary>
        public float? GetRate()
        {
            EnsureAttached();

            if (TryReadRate(out float rate))
                return rate;

            // Our view of the heap may simply be out of date - re-syncing it is
            // far cheaper than re-resolving, so try that first.
            RefreshHeap();

            if (TryReadRate(out rate))
                return rate;

            if (!TryResolveRateAddresses())
                return null;

            return TryReadRate(out rate, validateAddresses: false) ? rate : null;
        }

        /// <param name="validateAddresses">
        /// False only immediately after a resolve, which just walked this exact
        /// chain object by object - the addresses are known good at that instant,
        /// so re-checking them can only produce a false negative.
        /// </param>
        private bool TryReadRate(out float value, bool validateAddresses = true)
        {
            value = 0f;

            if (_rateGetterAddress == 0 || _dataTarget == null)
                return false;

            // The getter closure is long-lived, but "long-lived" is exactly what
            // a compacting GC relocates, so confirm it is still there.
            if (validateAddresses && !IsObjectOfType(_rateGetterAddress, _rateGetterTypeName, out _))
                return false;

            var reader = _dataTarget.DataReader;

            ulong refAddress = reader.ReadPointer(_rateGetterAddress + _rateGetterXFieldOffset);

            if (refAddress == 0)
                return false;

            // Not a ref cell any more means the closure field we read through no
            // longer holds what it did when we resolved it.
            if (validateAddresses && !IsObjectOfType(refAddress, _rateRefTypeName, out _))
                return false;

            byte[] buffer = new byte[4];
            if (reader.Read(refAddress + _rateRefContentsOffset, buffer) != 4)
                return false;

            float rate = BitConverter.ToSingle(buffer, 0);

            if (!float.IsFinite(rate) || rate < MinPlausibleRate || rate > MaxPlausibleRate)
                return false;

            value = rate;
            return true;
        }

        /// <summary>
        /// Heap walk that locates the rate getter and resolves it down to an
        /// object address plus field offsets. Runs on attach, and again whenever
        /// the cached addresses stop validating. Returns false (rather than
        /// throwing) when the game's state doesn't currently allow resolving -
        /// the next poll simply tries again.
        /// </summary>
        private bool TryResolveRateAddresses(bool force = false)
        {
            if (_runtime == null || _dataTarget == null)
                throw new InvalidOperationException("Not attached to a process.");

            if (!force && DateTime.UtcNow - _lastRateResolveAttempt < ResolveCooldown)
                return false;

            _lastRateResolveAttempt = DateTime.UtcNow;
            _rateGetterAddress = 0;

            RefreshHeap();

            var heap = _runtime.Heap;
            var reader = _dataTarget.DataReader;

            try
            {
                ulong settingAddress = 0;

                // One pass, not two: this used to walk the entire heap once to
                // find the rate callback and then walk it *again* to find the
                // trigger holding it. Matching the trigger's action by type
                // instead of by address does it in a single walk, and every heap
                // walk we avoid is one less chance to trip over a collection
                // happening underneath us.
                foreach (var obj in heap.EnumerateObjects())
                {
                    if (obj.Type == null || !obj.Type.Name.StartsWith(SettingTriggerTypePrefix, StringComparison.Ordinal))
                        continue;

                    ulong actionAddress = 0;
                    ulong candidateSetting = 0;

                    foreach (var field in obj.Type.Fields)
                    {
                        if (!field.IsObjectReference)
                            continue;

                        ulong reference = reader.ReadPointer(field.GetAddress(obj.Address));
                        if (reference == 0)
                            continue;

                        if (field.Name == "action")
                            actionAddress = reference;
                        else if (field.Name == "setting")
                            candidateSetting = reference;
                    }

                    if (candidateSetting == 0 || actionAddress == 0)
                        continue;

                    if (IsObjectOfType(actionAddress, RateCallbackTypeName, out _))
                    {
                        settingAddress = candidateSetting;
                        break;
                    }
                }

                if (settingAddress == 0)
                    return false;

                var setting = heap.GetObject(settingAddress);
                if (!setting.IsValid || setting.Type == null)
                    return false;

                var getField = setting.Type.Fields.FirstOrDefault(f => f.Name == "Get@" && f.IsObjectReference);
                if (getField == null)
                    return false;

                ulong getterAddress = reader.ReadPointer(getField.GetAddress(setting.Address));
                if (getterAddress == 0)
                    return false;

                var getter = heap.GetObject(getterAddress);
                if (!getter.IsValid || getter.Type == null)
                    return false;

                var xField = getter.Type.Fields.FirstOrDefault(f => f.Name == "x");
                if (xField == null)
                    return false;

                ulong refAddress = reader.ReadPointer(xField.GetAddress(getter.Address));
                if (refAddress == 0)
                    return false;

                var fsharpRef = heap.GetObject(refAddress);
                if (!fsharpRef.IsValid || fsharpRef.Type == null)
                    return false;

                var contentsField = fsharpRef.Type.Fields.FirstOrDefault(f => f.Name == "contents@");
                if (contentsField == null)
                    return false;

                // Offsets rather than absolute addresses wherever the address can
                // be recomputed - only the getter's own address has to be tracked,
                // and that one is re-validated by type on every read.
                _rateGetterAddress = getter.Address;
                _rateGetterXFieldOffset = xField.GetAddress(getter.Address) - getter.Address;
                _rateRefContentsOffset = contentsField.GetAddress(fsharpRef.Address) - fsharpRef.Address;
                _rateGetterTypeName = getter.Type.Name ?? "";
                _rateRefTypeName = fsharpRef.Type.Name ?? "";

                Console.WriteLine($"Resolved rate path: {_rateGetterTypeName} @ 0x{_rateGetterAddress:X} +0x{_rateGetterXFieldOffset:X} -> {_rateRefTypeName} +0x{_rateRefContentsOffset:X}");

                if (_rateGetterTypeName.Length == 0 || _rateRefTypeName.Length == 0)
                {
                    // Without a type name there's nothing to re-validate against
                    // later, so treat it as unresolved rather than caching an
                    // address we can never confirm.
                    _rateGetterAddress = 0;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                // Walking the heap of a running process can fail outright if the
                // game happens to be collecting while we look. Next poll retries.
                Console.WriteLine($"Rate resolve failed: {ex.Message}");
                _rateGetterAddress = 0;
                return false;
            }
        }

        // =====================================================================
        // SELECTED CHART
        // =====================================================================

        /// <summary>
        /// Reads the chart currently highlighted in song select. The FSharpRef
        /// holding its hash has no static home, so it has to be found by
        /// matching its value against a known hash; after that we read it
        /// directly, re-validating the cached address every time and rescanning
        /// when it no longer holds what we expect.
        /// </summary>
        public ChartInfo? GetSelectedChart()
        {
            EnsureAttached();

            RefreshHeap();

            string? selectedHash = ReadSelectedHash();

            if (selectedHash == null)
            {
                // The cached address didn't survive (GC moved it, or it was
                // never resolved) - go and find the ref again. The scan already
                // read the value it matched on, so take it from there rather
                // than reading (and re-validating) the same cell twice.
                TryResolveSelectedChartRef(out selectedHash);
            }

            LastSelectedHash = selectedHash;

            if (selectedHash == null)
                return null;

            // A hash we can read but don't recognise is a real reading, not a
            // stale one: the library snapshot just predates that chart being
            // imported. Leave the resolved address alone and let the caller
            // decide to refresh the library.
            return Charts.TryGetValue(selectedHash, out var chart) ? chart : null;
        }

        /// <summary>
        /// Reads through the cached ref address, returning null if anything
        /// along the way fails to validate - which means the address is stale
        /// and gets dropped so the next call re-resolves it.
        /// </summary>
        private string? ReadSelectedHash()
        {
            if (_selectedChartRefAddress == 0 || _dataTarget == null)
                return null;

            if (!IsObjectOfType(_selectedChartRefAddress, StringRefTypeName, out _))
            {
                _selectedChartRefAddress = 0;
                return null;
            }

            ulong stringAddress = _dataTarget.DataReader.ReadPointer(_selectedChartRefAddress + _selectedChartContentsOffset);
            if (stringAddress == 0)
                return null;

            string? value = ReadStringAt(stringAddress);

            if (value == null)
            {
                // Still a ref cell, but pointing at something that isn't a
                // string - so stop trusting this address either.
                _selectedChartRefAddress = 0;
                return null;
            }

            return value.Length == 0 ? null : value;
        }

        private bool TryResolveSelectedChartRef(out string? hash)
        {
            hash = null;

            if (_runtime == null || _dataTarget == null)
                throw new InvalidOperationException("Not attached to a process.");

            if (DateTime.UtcNow - _lastSelectedChartResolveAttempt < ResolveCooldown)
                return false;

            _lastSelectedChartResolveAttempt = DateTime.UtcNow;
            _selectedChartRefAddress = 0;

            // Nothing to match against - the scan identifies the ref purely by
            // its value being a hash we know.
            if (Charts.Count == 0)
                return false;

            var heap = _runtime.Heap;
            var reader = _dataTarget.DataReader;

            try
            {
                foreach (var obj in heap.EnumerateObjects())
                {
                    if (obj.Type?.Name != StringRefTypeName)
                        continue;

                    var contentsField = obj.Type.Fields.FirstOrDefault(f => f.Name == "contents@");
                    if (contentsField == null)
                        continue;

                    ulong contentsAddress = contentsField.GetAddress(obj.Address);
                    ulong reference = reader.ReadPointer(contentsAddress);
                    if (reference == 0)
                        continue;

                    string? value = ReadStringAt(reference);
                    if (value != null && Charts.ContainsKey(value))
                    {
                        _selectedChartRefAddress = obj.Address;
                        _selectedChartContentsOffset = contentsAddress - obj.Address;
                        hash = value;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Selected-chart resolve failed: {ex.Message}");
            }

            return false;
        }

        // =====================================================================
        // CHART LIBRARY
        // =====================================================================

        /// <summary>
        /// Resolves each ChartMeta field once, from the type - not by name-matching
        /// on every single object. After the first call, subsequent calls skip the
        /// heap walk entirely and just re-read the cached addresses, unless
        /// <paramref name="rescan"/> is set (e.g. after importing new songs) or
        /// those addresses no longer point at ChartMeta objects, which is what a
        /// compacting GC inside the game leaves behind.
        /// </summary>
        public Dictionary<string, ChartInfo> GetCharts(bool rescan = false)
        {
            EnsureAttached();

            RefreshHeap();

            var heap = _runtime!.Heap;
            var reader = _dataTarget!.DataReader;

            if (!rescan && _chartAddresses.Count > 0 && CachedChartAddressesLookValid())
                return Charts;

            var addresses = new Dictionary<string, ulong>();
            var charts = new Dictionary<string, ChartInfo>();

            // ClrType/ClrInstanceField instances don't survive a heap flush, so
            // they're resolved fresh here rather than kept in fields across calls.
            ClrType? chartMetaType = null;
            ClrInstanceField? hashField = null, titleField = null, artistField = null, difficultyField = null;

            foreach (var obj in heap.EnumerateObjects())
            {
                if (obj.Type?.Name != ChartMetaTypeName)
                    continue;

                if (chartMetaType != obj.Type)
                {
                    chartMetaType = obj.Type;
                    hashField = obj.Type.Fields.FirstOrDefault(f => f.Name == "Hash@");
                    titleField = obj.Type.Fields.FirstOrDefault(f => f.Name == "Title@");
                    artistField = obj.Type.Fields.FirstOrDefault(f => f.Name == "Artist@");
                    difficultyField = obj.Type.Fields.FirstOrDefault(f => f.Name == "DifficultyName@");
                }

                string? hash = ReadStringField(reader, obj.Address, hashField);
                if (string.IsNullOrEmpty(hash))
                    continue;

                addresses[hash] = obj.Address;
                charts[hash] = new ChartInfo
                {
                    Hash = hash,
                    Title = ReadStringField(reader, obj.Address, titleField) ?? "<unknown>",
                    Artist = ReadStringField(reader, obj.Address, artistField) ?? "<unknown>",
                    Difficulty = ReadStringField(reader, obj.Address, difficultyField) ?? "<unknown>",
                    Address = obj.Address
                };
            }

            // A scan that found nothing is a failed scan (mid-collection, say),
            // not an empty library - keep what we already had rather than
            // blanking the overlay.
            if (charts.Count == 0 && Charts.Count > 0)
                return Charts;

            _chartAddresses.Clear();
            foreach (var (hash, address) in addresses)
                _chartAddresses[hash] = address;

            Charts = charts;

            // The selection ref is identified by matching against Charts, so a
            // library that just changed shape is worth re-matching against -
            // right away, rather than after the usual re-resolve cooldown.
            _selectedChartRefAddress = 0;
            _lastSelectedChartResolveAttempt = DateTime.MinValue;

            return Charts;
        }

        /// <summary>
        /// Spot-checks the cached ChartMeta addresses. They all move together
        /// when the game compacts its heap, so a small sample is enough to tell
        /// whether the whole cache needs rebuilding.
        /// </summary>
        private bool CachedChartAddressesLookValid()
        {
            const int sampleSize = 8;

            int sampled = 0;

            foreach (var address in _chartAddresses.Values)
            {
                if (!IsObjectOfType(address, ChartMetaTypeName, out _))
                    return false;

                if (++sampled >= sampleSize)
                    break;
            }

            return true;
        }

        private string? ReadStringField(IDataReader reader, ulong objAddress, ClrInstanceField? field)
        {
            if (field == null || !field.IsObjectReference)
                return null;

            ulong reference = reader.ReadPointer(field.GetAddress(objAddress));
            if (reference == 0)
                return null;

            return ReadStringAt(reference);
        }
    }
}
