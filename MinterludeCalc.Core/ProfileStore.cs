using System.Text.Json;

namespace MinterludeCalc
{
    /// <summary>
    /// A named set of plays. Plays are claimed by whichever profile is active
    /// when they land, so a profile is "the plays I set while I was on it" -
    /// there's no way to retro-assign an old play to a profile it wasn't set on.
    /// </summary>
    public class Profile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public long CreatedUtc { get; set; }
        public List<long> ScoreIds { get; set; } = new();
    }

    /// <summary>
    /// Profiles and which one is active, persisted to the app data directory.
    ///
    /// "Main" is not a stored profile - it's the absence of a filter, i.e. every
    /// score in Interlude's database. That means it covers plays from every
    /// profile *and* everything from before profiles existed, and it can't drift
    /// out of sync with the real scores the way a maintained union would.
    /// </summary>
    public class ProfileStore
    {
        public const string MainProfileId = "main";
        public const string MainProfileName = "Main";

        private class ProfileFile
        {
            public List<Profile> Profiles { get; set; } = new();
            public string ActiveProfileId { get; set; } = MainProfileId;
        }

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly string _path;
        private readonly object _lock = new();
        private readonly List<Profile> _profiles = new();
        private string _activeProfileId = MainProfileId;

        public ProfileStore(string? path = null)
        {
            _path = path ?? AppData.PathTo("profiles.json");
            Load();
        }

        /// <summary>Every profile, Main first. Main is synthesised, not stored.</summary>
        public List<Profile> AllProfiles()
        {
            lock (_lock)
            {
                var all = new List<Profile>
                {
                    new() { Id = MainProfileId, Name = MainProfileName }
                };

                all.AddRange(_profiles.Select(p => new Profile
                {
                    Id = p.Id,
                    Name = p.Name,
                    CreatedUtc = p.CreatedUtc,
                    ScoreIds = p.ScoreIds.ToList()
                }));

                return all;
            }
        }

        public string ActiveProfileId
        {
            get { lock (_lock) return _activeProfileId; }
        }

        public string ActiveProfileName
        {
            get
            {
                lock (_lock)
                {
                    if (_activeProfileId == MainProfileId)
                        return MainProfileName;

                    return _profiles.FirstOrDefault(p => p.Id == _activeProfileId)?.Name ?? MainProfileName;
                }
            }
        }

        public bool IsMainActive => ActiveProfileId == MainProfileId;

        public Profile CreateProfile(string name)
        {
            name = name.Trim();

            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Profile name can't be empty.", nameof(name));

            if (string.Equals(name, MainProfileName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"'{MainProfileName}' is reserved - it always means every score.", nameof(name));

            Profile profile;

            lock (_lock)
            {
                if (_profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException($"A profile called '{name}' already exists.", nameof(name));

                profile = new Profile { Name = name, CreatedUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                _profiles.Add(profile);
                _activeProfileId = profile.Id;
            }

            Save();
            return profile;
        }

        public void DeleteProfile(string id)
        {
            if (id == MainProfileId)
                throw new InvalidOperationException($"'{MainProfileName}' can't be deleted.");

            lock (_lock)
            {
                _profiles.RemoveAll(p => p.Id == id);

                if (_activeProfileId == id)
                    _activeProfileId = MainProfileId;
            }

            Save();
        }

        public void SetActiveProfile(string id)
        {
            lock (_lock)
            {
                if (id != MainProfileId && _profiles.All(p => p.Id != id))
                    throw new ArgumentException($"No profile with id '{id}'.", nameof(id));

                if (_activeProfileId == id)
                    return;

                _activeProfileId = id;
            }

            Save();
        }

        /// <summary>
        /// Claims a newly-set play for the active profile. A no-op on Main,
        /// which doesn't track ids - it's defined as "everything" instead.
        /// </summary>
        public void RecordScore(long scoreId)
        {
            lock (_lock)
            {
                if (_activeProfileId == MainProfileId)
                    return;

                var profile = _profiles.FirstOrDefault(p => p.Id == _activeProfileId);
                if (profile == null || profile.ScoreIds.Contains(scoreId))
                    return;

                profile.ScoreIds.Add(scoreId);
            }

            Save();
        }

        /// <summary>
        /// A predicate selecting the plays that belong to a profile. Null means
        /// "no filtering needed" (Main), which lets callers skip the check
        /// entirely on the common path.
        /// </summary>
        public Func<ScoreRecord, bool>? FilterFor(string profileId)
        {
            if (profileId == MainProfileId)
                return null;

            HashSet<long> ids;

            lock (_lock)
            {
                var profile = _profiles.FirstOrDefault(p => p.Id == profileId);

                // An unknown profile selects nothing rather than everything -
                // silently showing Main's numbers under another name would be
                // worse than showing an empty profile.
                ids = profile == null ? new HashSet<long>() : new HashSet<long>(profile.ScoreIds);
            }

            return score => ids.Contains(score.Id);
        }

        public int ScoreCount(string profileId)
        {
            if (profileId == MainProfileId)
                return -1; // "all of them" - the caller knows the real total.

            lock (_lock)
                return _profiles.FirstOrDefault(p => p.Id == profileId)?.ScoreIds.Count ?? 0;
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path))
                    return;

                var file = JsonSerializer.Deserialize<ProfileFile>(File.ReadAllText(_path));
                if (file == null)
                    return;

                lock (_lock)
                {
                    _profiles.Clear();
                    _profiles.AddRange(file.Profiles.Where(p => !string.IsNullOrEmpty(p.Id)));

                    _activeProfileId = file.ActiveProfileId == MainProfileId || _profiles.Any(p => p.Id == file.ActiveProfileId)
                        ? file.ActiveProfileId
                        : MainProfileId;
                }
            }
            catch
            {
                // Rather than refuse to start, fall back to Main-only. The file
                // is rewritten on the next change.
            }
        }

        private void Save()
        {
            ProfileFile file;

            lock (_lock)
            {
                file = new ProfileFile
                {
                    ActiveProfileId = _activeProfileId,
                    Profiles = _profiles.Select(p => new Profile
                    {
                        Id = p.Id,
                        Name = p.Name,
                        CreatedUtc = p.CreatedUtc,
                        ScoreIds = p.ScoreIds.ToList()
                    }).ToList()
                };
            }

            try
            {
                AppData.EnsureDirectory();

                string temporary = _path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(file, JsonOptions));
                File.Move(temporary, _path, overwrite: true);
            }
            catch
            {
                // Losing the write means the profile is in-memory only for this
                // session; not worth tearing the app down over.
            }
        }
    }
}
