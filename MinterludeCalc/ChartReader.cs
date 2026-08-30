using Microsoft.Diagnostics.Runtime;
using System.Diagnostics;
using System.Text.Json;

namespace MinterludeCalc
{
    internal class ChartReader
    {
        const string ProcessName = "Interlude";

        private DataTarget? _dataTarget;
        private ClrRuntime? _runtime;

        // ---- Rate reading: resolved once, then two direct reads per call ----
        // Absolute address of the getter closure's "x" field. The getter object
        // itself is a long-lived singleton, so this address is fixed for the
        // lifetime of the process.
        private ulong _rateGetterXFieldAddr;
        // Offset of "contents@" inside FSharpRef<float>. Fixed per-type, but we
        // add it to a freshly-read ref address every call (the ref cell content
        // pointer at _rateGetterXFieldAddr is re-read live each time).
        private ulong _rateRefContentsOffset;

        // ---- Selected chart: resolved once we've seen a valid selection ----
        private ulong _selectedChartRefAddress;
        private ulong _selectedChartContentsOffset;

        // ---- Chart library: field offsets resolved once from the type ----
        private ClrType? _chartMetaType;
        private ClrInstanceField? _hashField, _titleField, _artistField, _difficultyField, _audioField;
        private ClrType? _audioValueType;
        private ClrInstanceField? _audioInnerStringField;
        private readonly Dictionary<string, ulong> _chartAddresses = new();

        public Dictionary<string, ChartInfo> Charts { get; private set; } = new();

        /// <summary>
        /// Interlude's working directory (where Songs/charts.db, Songs/.assets,
        /// etc. live). Resolved once during Attach() - see ResolveWorkingDirectory.
        /// </summary>
        public string WorkingDirectory { get; private set; } = "";

        public void Attach()
        {
            var process = Process.GetProcessesByName(ProcessName).FirstOrDefault();
            if (process == null)
                throw new InvalidOperationException($"Process '{ProcessName}' not found.");

            _dataTarget = DataTarget.AttachToProcess(process.Id, suspend: false);

            var runtimeInfo = _dataTarget.ClrVersions.FirstOrDefault()
                ?? throw new InvalidOperationException("No CLR runtime found in target process.");

            _runtime = runtimeInfo.CreateRuntime();

            if (!_runtime.Heap.CanWalkHeap)
                throw new InvalidOperationException("Cannot walk heap.");

            WorkingDirectory = ResolveWorkingDirectory(process);

            ResolveRateAddresses();
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

        /// <summary>
        /// Cheap way to make sure subsequent reads see current process state,
        /// without paying the cost of tearing down and recreating the runtime.
        /// </summary>
        private void RefreshHeap()
        {
            if (_runtime == null)
                throw new InvalidOperationException("Not attached to a process.");

            _runtime.FlushCachedData();
        }

        // =====================================================================
        // RATE
        // =====================================================================

        /// <summary>
        /// One-time heap walk that locates the rate getter and resolves it all
        /// the way down to a fixed field address + offset. After this runs,
        /// GetRate() never touches the heap enumerator again.
        /// </summary>
        private void ResolveRateAddresses()
        {
            if (_runtime == null || _dataTarget == null)
                throw new InvalidOperationException("Not attached to a process.");

            var heap = _runtime.Heap;
            var reader = _dataTarget.DataReader;

            ulong rateCallbackAddress = 0;
            foreach (var obj in heap.EnumerateObjects())
            {
                if (obj.Type?.Name == "Interlude.Features.Gameplay.SelectedChart+rate@366")
                {
                    rateCallbackAddress = obj.Address;
                    break;
                }
            }

            if (rateCallbackAddress == 0)
                throw new InvalidOperationException("Could not find rate callback.");

            ulong settingAddress = 0;
            foreach (var obj in heap.EnumerateObjects())
            {
                if (obj.Type == null || !obj.Type.Name.StartsWith("Percyqaz.Common.Setting+trigger@105", StringComparison.Ordinal))
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

                if (actionAddress == rateCallbackAddress && candidateSetting != 0)
                {
                    settingAddress = candidateSetting;
                    break;
                }
            }

            if (settingAddress == 0)
                throw new InvalidOperationException("Could not find rate setting.");

            var setting = heap.GetObject(settingAddress);
            if (!setting.IsValid || setting.Type == null)
                throw new InvalidOperationException("Rate setting object is invalid.");

            var getField = setting.Type.Fields.FirstOrDefault(f => f.Name == "Get@" && f.IsObjectReference)
                ?? throw new InvalidOperationException("Could not find Get@ field on rate setting.");

            ulong getterAddress = reader.ReadPointer(getField.GetAddress(setting.Address));
            if (getterAddress == 0)
                throw new InvalidOperationException("Rate getter address was null.");

            var getter = heap.GetObject(getterAddress);
            if (!getter.IsValid || getter.Type == null)
                throw new InvalidOperationException("Rate getter object is invalid.");

            var xField = getter.Type.Fields.FirstOrDefault(f => f.Name == "x")
                ?? throw new InvalidOperationException("Could not find 'x' field on rate getter.");

            _rateGetterXFieldAddr = xField.GetAddress(getter.Address);

            ulong refAddress = reader.ReadPointer(_rateGetterXFieldAddr);
            if (refAddress == 0)
                throw new InvalidOperationException("Rate FSharpRef address was null.");

            var fsharpRef = heap.GetObject(refAddress);
            if (!fsharpRef.IsValid || fsharpRef.Type == null)
                throw new InvalidOperationException("Rate FSharpRef object is invalid.");

            var contentsField = fsharpRef.Type.Fields.FirstOrDefault(f => f.Name == "contents@")
                ?? throw new InvalidOperationException("Could not find 'contents@' field on rate FSharpRef.");

            _rateRefContentsOffset = contentsField.GetAddress(fsharpRef.Address) - fsharpRef.Address;

            Console.WriteLine($"Resolved rate path: getter.x @ 0x{_rateGetterXFieldAddr:X}, contents offset 0x{_rateRefContentsOffset:X}");
        }

        /// <summary>
        /// Reads the current rate with two direct memory reads - no heap
        /// enumeration, no per-call runtime creation.
        /// </summary>
        public float? GetRate()
        {
            if (_dataTarget == null)
                throw new InvalidOperationException("Not attached to a process.");
            if (_rateGetterXFieldAddr == 0)
                throw new InvalidOperationException("Rate getter was not resolved.");

            var reader = _dataTarget.DataReader;

            ulong refAddress = reader.ReadPointer(_rateGetterXFieldAddr);
            if (refAddress == 0)
                return null;

            ulong contentsAddress = refAddress + _rateRefContentsOffset;

            byte[] buffer = new byte[4];
            if (reader.Read(contentsAddress, buffer) != 4)
                return null;

            return BitConverter.ToSingle(buffer, 0);
        }

        // =====================================================================
        // SELECTED CHART
        // =====================================================================

        /// <summary>
        /// First call does a one-time scan to identify which FSharpRef&lt;string&gt;
        /// holds the selected chart's hash (there's no static home for it, so it
        /// has to be found by matching its current value against a known hash).
        /// Every call after that just reads the pointer directly.
        /// </summary>
        public ChartInfo? GetSelectedChart()
        {
            if (_runtime == null || _dataTarget == null)
                throw new InvalidOperationException("Not attached to a process.");

            RefreshHeap();
            var heap = _runtime.Heap;
            var reader = _dataTarget.DataReader;

            ulong stringAddress;

            if (_selectedChartRefAddress != 0)
            {
                // Fast path: direct read, no enumeration.
                ulong contentsAddress = _selectedChartRefAddress + _selectedChartContentsOffset;
                stringAddress = reader.ReadPointer(contentsAddress);
            }
            else
            {
                // Slow path, runs once.
                stringAddress = 0;

                foreach (var obj in heap.EnumerateObjects())
                {
                    if (obj.Type?.Name != "Microsoft.FSharp.Core.FSharpRef<System.String>")
                        continue;

                    var contentsField = obj.Type.Fields.FirstOrDefault(f => f.Name == "contents@");
                    if (contentsField == null)
                        continue;

                    ulong contentsAddress = contentsField.GetAddress(obj.Address);
                    ulong reference = reader.ReadPointer(contentsAddress);
                    if (reference == 0)
                        continue;

                    var valueObject = heap.GetObject(reference);
                    if (!valueObject.IsValid || valueObject.Type?.Name != "System.String")
                        continue;

                    string? value = valueObject.AsString();
                    if (value != null && Charts.ContainsKey(value))
                    {
                        _selectedChartRefAddress = obj.Address;
                        _selectedChartContentsOffset = contentsAddress - obj.Address;
                        stringAddress = reference;
                        break;
                    }
                }
            }

            if (stringAddress == 0)
                return null;

            var strObj = heap.GetObject(stringAddress);
            if (!strObj.IsValid)
                return null;

            string? selectedHash = strObj.AsString();
            if (selectedHash == null || !Charts.TryGetValue(selectedHash, out var chart))
                return null;

            return chart;
        }

        // =====================================================================
        // CHART LIBRARY
        // =====================================================================

        /// <summary>
        /// Resolves each ChartMeta field once, from the type - not by name-matching
        /// on every single object. After the first call, subsequent calls skip the
        /// heap walk entirely and just re-read the cached addresses, unless
        /// <paramref name="rescan"/> is set (e.g. after importing new songs).
        /// </summary>
        public Dictionary<string, ChartInfo> GetCharts(bool rescan = false)
        {
            if (_runtime == null || _dataTarget == null)
                throw new InvalidOperationException("Not attached to a process.");

            var filePathCache = LoadFileCache();

            RefreshHeap();
            var heap = _runtime.Heap;
            var reader = _dataTarget.DataReader;

            if (rescan || _chartAddresses.Count == 0)
            {
                _chartAddresses.Clear();

                foreach (var obj in heap.EnumerateObjects())
                {
                    if (obj.Type?.Name != "Prelude.Data.Library.ChartMeta")
                        continue;

                    if (_chartMetaType != obj.Type)
                        ResolveChartMetaFields(obj.Type);

                    string? hash = ReadStringField(heap, reader, obj.Address, _hashField);
                    if (!string.IsNullOrEmpty(hash))
                        _chartAddresses[hash] = obj.Address;
                }
            }

            Charts = new();

            foreach (var (hash, address) in _chartAddresses)
            {
                var obj = heap.GetObject(address);
                if (!obj.IsValid)
                    continue;

                string? title = ReadStringField(heap, reader, address, _titleField);
                string? artist = ReadStringField(heap, reader, address, _artistField);
                string? difficulty = ReadStringField(heap, reader, address, _difficultyField);
                string? audio = ReadAudioField(heap, reader, address);

                if (string.IsNullOrEmpty(difficulty))
                    continue;

                string cacheKey = $"{hash}:{difficulty}";
                string file;

                if (filePathCache.TryGetValue(cacheKey, out var cachedFile))
                {
                    file = cachedFile;
                }
                else
                {
                    var directory = Path.GetDirectoryName(audio);
                    file = string.IsNullOrEmpty(directory) ? "" : GetChartFileFromDifficulty(directory, difficulty);
                    filePathCache[cacheKey] = file;
                }

                Charts[hash] = new ChartInfo
                {
                    Hash = hash,
                    Title = title ?? "<unknown>",
                    Artist = artist ?? "<unknown>",
                    Difficulty = difficulty ?? "<unknown>",
                    Audio = audio ?? "<unknown>",
                    // Deliberately left empty (not "<unknown>") when no linked .osu/.sm
                    // file was found - Application.cs uses that to fall back to reading
                    // the chart natively from Interlude's own charts.db.
                    File = file,
                    Address = address
                };
            }

            SaveFileCache(filePathCache);
            return Charts;
        }

        /// <summary>Resolves and caches ChartMeta's field layout once, from the type itself.</summary>
        private void ResolveChartMetaFields(ClrType type)
        {
            _chartMetaType = type;
            _hashField = type.Fields.FirstOrDefault(f => f.Name == "Hash@");
            _titleField = type.Fields.FirstOrDefault(f => f.Name == "Title@");
            _artistField = type.Fields.FirstOrDefault(f => f.Name == "Artist@");
            _difficultyField = type.Fields.FirstOrDefault(f => f.Name == "DifficultyName@");
            _audioField = type.Fields.FirstOrDefault(f => f.Name == "Audio@");

            // Force a re-resolve of the nested Audio field on the next read,
            // in case the layout differs from a previous library's type.
            _audioValueType = null;
            _audioInnerStringField = null;
        }

        private static string? ReadStringField(ClrHeap heap, IDataReader reader, ulong objAddress, ClrInstanceField? field)
        {
            if (field == null || !field.IsObjectReference)
                return null;

            ulong reference = reader.ReadPointer(field.GetAddress(objAddress));
            if (reference == 0)
                return null;

            var valueObject = heap.GetObject(reference);
            if (!valueObject.IsValid || valueObject.Type?.Name != "System.String")
                return null;

            return valueObject.AsString();
        }

        private string? ReadAudioField(ClrHeap heap, IDataReader reader, ulong objAddress)
        {
            if (_audioField == null)
                return null;

            ulong audioReference = reader.ReadPointer(_audioField.GetAddress(objAddress));
            if (audioReference == 0)
                return null;

            var audioValueObject = heap.GetObject(audioReference);
            if (!audioValueObject.IsValid || audioValueObject.Type == null)
                return null;

            // Resolve which field of the Audio value actually holds the string,
            // once per distinct type, instead of walking all its fields every call.
            if (_audioInnerStringField == null || _audioValueType != audioValueObject.Type)
            {
                _audioValueType = audioValueObject.Type;
                _audioInnerStringField = null;

                foreach (var f in audioValueObject.Type.Fields)
                {
                    if (!f.IsObjectReference)
                        continue;

                    ulong candidateRef = reader.ReadPointer(f.GetAddress(audioValueObject.Address));
                    if (candidateRef == 0)
                        continue;

                    var candidateObj = heap.GetObject(candidateRef);
                    if (candidateObj.IsValid && candidateObj.Type?.Name == "System.String")
                    {
                        _audioInnerStringField = f;
                        break;
                    }
                }
            }

            if (_audioInnerStringField == null)
                return null;

            ulong stringRef = reader.ReadPointer(_audioInnerStringField.GetAddress(audioValueObject.Address));
            if (stringRef == 0)
                return null;

            var strObj = heap.GetObject(stringRef);
            return strObj.IsValid ? strObj.AsString() : null;
        }

        // =====================================================================
        // FILE CACHE + DISK LOOKUP (unrelated to heap reading, unchanged in spirit)
        // =====================================================================

        private static Dictionary<string, string> LoadFileCache()
        {
            if (!File.Exists("cache.json"))
            {
                File.WriteAllText("cache.json", "{}");
                return new();
            }

            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText("cache.json"))
                ?? new();
        }

        private static void SaveFileCache(Dictionary<string, string> cache)
        {
            File.WriteAllText("cache.json", JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));
        }

        private string GetChartFileFromDifficulty(string directory, string difficulty)
        {
            var files = Directory.GetFiles(directory, "*.*", SearchOption.TopDirectoryOnly);

            foreach (var file in files)
            {
                try
                {
                    if (Path.GetExtension(file).Equals(".osu", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var line in File.ReadLines(file))
                        {
                            if (line.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
                            {
                                var chartDifficulty = line["Version:".Length..].Trim();

                                if (chartDifficulty.Equals(difficulty, StringComparison.OrdinalIgnoreCase))
                                    return file;

                                break;
                            }
                        }
                    }
                    else if (Path.GetExtension(file).Equals(".sm", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = difficulty.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                        if (parts.Length < 3)
                            continue;

                        var difficultyType = parts[^2];
                        var difficultyRating = parts[^1];

                        var lines = File.ReadAllLines(file);

                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (!lines[i].Trim().Equals("#NOTES:", StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (i + 3 >= lines.Length)
                                continue;

                            var smDifficulty = lines[i + 3].Trim().TrimEnd(':');
                            var smRating = lines[i + 4].Trim().TrimEnd(':');

                            if (smDifficulty.Equals(difficultyType, StringComparison.OrdinalIgnoreCase) &&
                                smRating.Equals(difficultyRating, StringComparison.OrdinalIgnoreCase))
                            {
                                return file;
                            }
                        }
                    }
                }
                catch
                {
                }
            }

            return string.Empty;
        }
    }
}