using Microsoft.Data.Sqlite;

namespace MinterludeCalc
{
    /// <summary>A single row from Interlude's scores table - one recorded play.</summary>
    public class ScoreRecord
    {
        public long Id { get; set; }
        public string ChartId { get; set; } = "";
        public long Timestamp { get; set; }
        public byte[] ReplayBlob { get; set; } = Array.Empty<byte>();
        public float Rate { get; set; }
        public int Keys { get; set; }
        public bool IsFailed { get; set; }

        /// <summary>Raw ModState JSON as stored by Interlude, e.g. "{}" or "{\"mirror\":0}". See ScoreMods.</summary>
        public string Mods { get; set; } = "{}";
    }

    /// <summary>
    /// Reads Interlude's user-data scores database, at &lt;WorkingDirectory&gt;/Data/scores.db
    /// (per prelude/src/Data/Library/Library.fs: init_score_db uses "Data/scores.db").
    /// Every row stores raw replay input, not a precomputed accuracy - see ScoringEngine.
    /// </summary>
    public class ScoresDatabaseReader
    {
        private readonly string _databasePath;

        public ScoresDatabaseReader(string gameWorkingDirectory)
        {
            _databasePath = Path.Combine(gameWorkingDirectory, "Data", "scores.db");
        }

        public bool DatabaseExists => File.Exists(_databasePath);

        private SqliteConnection OpenConnection()
        {
            if (!DatabaseExists)
                throw new FileNotFoundException($"Interlude scores database not found at '{_databasePath}'.");

            var connection = new SqliteConnection($"Data Source={_databasePath};Mode=ReadOnly");
            connection.Open();
            return connection;
        }

        /// <summary>All scores for a given chart hash.</summary>
        public List<ScoreRecord> GetScoresForChart(string chartId)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT Id, ChartId, Timestamp, Replay, Rate, Keys, IsFailed, Mods FROM scores WHERE ChartId = @chartId;";
            command.Parameters.AddWithValue("@chartId", chartId);

            return ReadAll(command);
        }

        /// <summary>Every score in the database - used to build player rating across the whole library.</summary>
        public List<ScoreRecord> GetAllScores()
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, ChartId, Timestamp, Replay, Rate, Keys, IsFailed, Mods FROM scores;";

            return ReadAll(command);
        }

        /// <summary>
        /// Every score, without its replay - the blobs are by far the biggest
        /// part of the table and are only needed for plays that actually have to
        /// be rescored. Records come back with an empty ReplayBlob; fetch the
        /// ones you need individually with <see cref="GetScoreById"/>.
        /// </summary>
        public List<ScoreRecord> GetScoreIndex()
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, ChartId, Timestamp, Rate, Keys, IsFailed, Mods FROM scores;";

            var results = new List<ScoreRecord>();

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new ScoreRecord
                {
                    Id = reader.GetInt64(0),
                    ChartId = reader.GetString(1),
                    Timestamp = reader.GetInt64(2),
                    Rate = reader.GetFloat(3),
                    Keys = reader.GetInt32(4),
                    IsFailed = reader.GetInt32(5) != 0,
                    Mods = reader.GetString(6)
                });
            }

            return results;
        }

        /// <summary>One score, replay included.</summary>
        public ScoreRecord? GetScoreById(long id)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT Id, ChartId, Timestamp, Replay, Rate, Keys, IsFailed, Mods FROM scores WHERE Id = @id;";
            command.Parameters.AddWithValue("@id", id);

            return ReadAll(command).FirstOrDefault();
        }

        /// <summary>The single most recent score by Id - used to detect "you just got a new play".</summary>
        public ScoreRecord? GetMostRecentScore()
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT Id, ChartId, Timestamp, Replay, Rate, Keys, IsFailed, Mods FROM scores ORDER BY Id DESC LIMIT 1;";

            return ReadAll(command).FirstOrDefault();
        }

        /// <summary>The highest score Id currently in the database (for cheaply polling "has anything new appeared").</summary>
        public long GetMaxScoreId()
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT MAX(Id) FROM scores;";

            var result = command.ExecuteScalar();
            return result == null || result is DBNull ? 0L : Convert.ToInt64(result);
        }

        private static List<ScoreRecord> ReadAll(SqliteCommand command)
        {
            var results = new List<ScoreRecord>();

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new ScoreRecord
                {
                    Id = reader.GetInt64(0),
                    ChartId = reader.GetString(1),
                    Timestamp = reader.GetInt64(2),
                    ReplayBlob = (byte[])reader[3],
                    Rate = reader.GetFloat(4),
                    Keys = reader.GetInt32(5),
                    IsFailed = reader.GetInt32(6) != 0,
                    Mods = reader.GetString(7)
                });
            }

            return results;
        }
    }
}
