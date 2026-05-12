using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Pipeline.Core;

/// <summary>
/// SQLite data access layer for the monitor service. Tracks builds, test failures,
/// triage requests, and filed issues.
/// </summary>
public sealed class MonitorClient : IDisposable
{
    private readonly SqliteConnection _connection;

    private MonitorClient(SqliteConnection connection)
    {
        _connection = connection;
    }

    public static MonitorClient Open(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        var db = new MonitorClient(connection);
        db.EnsureSchema();
        return db;
    }

    private void EnsureSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS builds (
                id INTEGER PRIMARY KEY,
                azdo_build_id INTEGER NOT NULL UNIQUE,
                repository TEXT NOT NULL,
                build_number TEXT NOT NULL,
                source_branch TEXT NOT NULL,
                definition_name TEXT NOT NULL,
                status TEXT NOT NULL,
                result TEXT,
                finish_time TEXT,
                has_test_failures INTEGER DEFAULT 0,
                azdo_failure_state TEXT NOT NULL DEFAULT 'pending',
                helix_failure_state TEXT NOT NULL DEFAULT 'pending',
                collection_failures INTEGER NOT NULL DEFAULT 0,
                last_collection_attempt TEXT,
                timeline_json TEXT,
                triage_state TEXT NOT NULL DEFAULT 'pending',
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS test_failures (
                id INTEGER PRIMARY KEY,
                build_id INTEGER NOT NULL REFERENCES builds(id),
                test_name TEXT NOT NULL,
                outcome TEXT NOT NULL,
                error_message TEXT,
                stack_trace TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS helix_work_items (
                id INTEGER PRIMARY KEY,
                build_id INTEGER NOT NULL REFERENCES builds(id),
                friendly_name TEXT NOT NULL,
                exit_code INTEGER NOT NULL,
                machine_name TEXT NOT NULL,
                queue_name TEXT NOT NULL,
                console_uri TEXT NOT NULL,
                job_id INTEGER NOT NULL,
                work_item_id INTEGER NOT NULL,
                status TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS triage_requests (
                id INTEGER PRIMARY KEY,
                build_id INTEGER NOT NULL REFERENCES builds(id),
                repository TEXT NOT NULL,
                pr_number INTEGER,
                status TEXT NOT NULL DEFAULT 'pending',
                result_json TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                completed_at TEXT
            );

            CREATE TABLE IF NOT EXISTS flaky_tests (
                id INTEGER PRIMARY KEY,
                test_name TEXT NOT NULL,
                repository TEXT NOT NULL,
                first_seen TEXT NOT NULL DEFAULT (datetime('now')),
                last_seen TEXT NOT NULL DEFAULT (datetime('now')),
                occurrence_count INTEGER NOT NULL DEFAULT 1,
                rationale TEXT,
                issue_number INTEGER,
                issue_url TEXT,
                UNIQUE(test_name, repository)
            );

            CREATE TABLE IF NOT EXISTS fix_requests (
                id INTEGER PRIMARY KEY,
                triage_request_id INTEGER NOT NULL REFERENCES triage_requests(id),
                repository TEXT NOT NULL,
                test_name TEXT NOT NULL,
                diagnosis TEXT NOT NULL,
                proposed_fix TEXT,
                issue_url TEXT,
                status TEXT NOT NULL DEFAULT 'pending',
                pr_url TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                completed_at TEXT
            );

            CREATE TABLE IF NOT EXISTS triage_chat_logs (
                id INTEGER PRIMARY KEY,
                triage_request_id INTEGER NOT NULL REFERENCES triage_requests(id),
                chat_log TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE INDEX IF NOT EXISTS idx_triage_chat_logs_request ON triage_chat_logs(triage_request_id);
            CREATE INDEX IF NOT EXISTS idx_builds_azdo ON builds(azdo_build_id);
            CREATE INDEX IF NOT EXISTS idx_builds_repo ON builds(repository);
            CREATE INDEX IF NOT EXISTS idx_builds_collection ON builds(azdo_failure_state, helix_failure_state);
            CREATE INDEX IF NOT EXISTS idx_builds_triage ON builds(triage_state);
            CREATE INDEX IF NOT EXISTS idx_test_failures_build ON test_failures(build_id);
            CREATE INDEX IF NOT EXISTS idx_helix_work_items_build ON helix_work_items(build_id);
            CREATE INDEX IF NOT EXISTS idx_triage_requests_status ON triage_requests(status);
            CREATE INDEX IF NOT EXISTS idx_fix_requests_status ON fix_requests(status);
            CREATE INDEX IF NOT EXISTS idx_flaky_tests_name ON flaky_tests(test_name, repository);
            """;
        cmd.ExecuteNonQuery();

        // Migrations for columns added after initial schema
        MigrateAddColumn("helix_work_items", "console_summary", "TEXT");
        MigrateAddColumn("builds", "azdo_collection_state", "INTEGER NOT NULL DEFAULT 0");
        MigrateAddColumn("builds", "helix_collection_state", "INTEGER NOT NULL DEFAULT 0");
        MigrateAddColumn("test_failures", "comment", "TEXT");
        MigrateAddColumn("flaky_tests", "rationale", "TEXT");
    }

    /// <summary>
    /// Safely adds a column to an existing table. No-op if the column already exists.
    /// </summary>
    private void MigrateAddColumn(string table, string column, string type)
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
            cmd.ExecuteNonQuery();
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Column already exists — ignore
        }
    }

    /// <summary>
    /// Returns true if a build with this AzDO build ID has already been recorded.
    /// </summary>
    public bool HasBuild(int azdoBuildId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM builds WHERE azdo_build_id = @id";
        cmd.Parameters.AddWithValue("@id", azdoBuildId);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    /// <summary>
    /// Returns true if any test failures exist for a build identified by its AzDO build ID.
    /// </summary>
    public bool HasTestFailuresForAzdoBuild(int azdoBuildId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(1) FROM test_failures tf
            JOIN builds b ON b.id = tf.build_id
            WHERE b.azdo_build_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", azdoBuildId);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    /// <summary>
    /// Gets the internal row ID for a build by its AzDO build ID.
    /// </summary>
    public long? GetBuildRowId(int azdoBuildId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM builds WHERE azdo_build_id = @id";
        cmd.Parameters.AddWithValue("@id", azdoBuildId);
        var result = cmd.ExecuteScalar();
        return result is long id ? id : null;
    }

    /// <summary>
    /// Updates the has_test_failures flag on a build.
    /// </summary>
    public void UpdateHasTestFailures(long buildId, bool hasTestFailures)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE builds SET has_test_failures = @val WHERE id = @id";
        cmd.Parameters.AddWithValue("@val", hasTestFailures ? 1 : 0);
        cmd.Parameters.AddWithValue("@id", buildId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Stores the timeline summary JSON blob for a build.
    /// </summary>
    public void SetTimelineData(long buildId, string timelineJson)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE builds SET timeline_json = @json WHERE id = @id";
        cmd.Parameters.AddWithValue("@json", timelineJson);
        cmd.Parameters.AddWithValue("@id", buildId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Gets the timeline summary for a build by AzDO build ID. Returns null if not yet collected.
    /// </summary>
    public MonitorTimelineSummary? GetTimelineData(int azdoBuildId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT timeline_json FROM builds WHERE azdo_build_id = @id";
        cmd.Parameters.AddWithValue("@id", azdoBuildId);
        var result = cmd.ExecuteScalar();
        if (result is string json)
        {
            return System.Text.Json.JsonSerializer.Deserialize<MonitorTimelineSummary>(json);
        }
        return null;
    }

    /// <summary>
    /// Records a build and returns its database row ID.
    /// </summary>
    public long InsertBuild(
        int azdoBuildId,
        string repository,
        string buildNumber,
        string sourceBranch,
        string definitionName,
        string status,
        string? result,
        DateTime? finishTime,
        bool? hasTestFailures = null)
    {
        // Succeeded builds don't need failure collection or triage
        var azdoState = result == "succeeded" ? "collected" : "pending";
        var helixState = result == "succeeded" ? "collected" : "pending";
        var triageState = result == "succeeded" ? "skipped" : "pending";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO builds (azdo_build_id, repository, build_number, source_branch, definition_name, status, result, finish_time, has_test_failures, azdo_failure_state, helix_failure_state, triage_state)
            VALUES (@azdoBuildId, @repository, @buildNumber, @sourceBranch, @definitionName, @status, @result, @finishTime, @hasTestFailures, @azdoState, @helixState, @triageState);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@azdoBuildId", azdoBuildId);
        cmd.Parameters.AddWithValue("@repository", repository);
        cmd.Parameters.AddWithValue("@buildNumber", buildNumber);
        cmd.Parameters.AddWithValue("@sourceBranch", sourceBranch);
        cmd.Parameters.AddWithValue("@definitionName", definitionName);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@result", (object?)result ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@finishTime", finishTime?.ToString("o") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@hasTestFailures", hasTestFailures.HasValue ? (object)(hasTestFailures.Value ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("@azdoState", azdoState);
        cmd.Parameters.AddWithValue("@helixState", helixState);
        cmd.Parameters.AddWithValue("@triageState", triageState);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Records a test failure linked to a build.
    /// </summary>
    public void InsertTestFailure(long buildId, string testName, string outcome, string? errorMessage, string? stackTrace = null, string? comment = null)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO test_failures (build_id, test_name, outcome, error_message, stack_trace, comment)
            VALUES (@buildId, @testName, @outcome, @errorMessage, @stackTrace, @comment);
            """;
        cmd.Parameters.AddWithValue("@buildId", buildId);
        cmd.Parameters.AddWithValue("@testName", testName);
        cmd.Parameters.AddWithValue("@outcome", outcome);
        cmd.Parameters.AddWithValue("@errorMessage", (object?)errorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@stackTrace", (object?)stackTrace ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@comment", (object?)comment ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns distinct Helix job names extracted from test failure comments for a build.
    /// Helix embeds JSON like {"HelixJobId":"guid","HelixWorkItemName":"name"} in AzDO test result comments.
    /// Note: despite the JSON field being called "HelixJobId", the value is actually the Helix job name (a GUID string),
    /// not the numeric job ID.
    /// </summary>
    public List<string> GetHelixJobNamesFromComments(long buildId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT comment FROM test_failures
            WHERE build_id = @buildId
            AND comment IS NOT NULL
            AND comment LIKE '%HelixJobId%'
            """;
        cmd.Parameters.AddWithValue("@buildId", buildId);

        var jobNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var comment = reader.GetString(0);
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(comment);
                if (doc.RootElement.TryGetProperty("HelixJobId", out var jobIdElement))
                {
                    var jobId = jobIdElement.GetString();
                    if (!string.IsNullOrEmpty(jobId))
                        jobNames.Add(jobId);
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Not valid JSON — skip
            }
        }

        return jobNames.ToList();
    }

    /// <summary>
    public long InsertTriageRequest(long buildId, string repository, int? prNumber)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO triage_requests (build_id, repository, pr_number)
            VALUES (@buildId, @repository, @prNumber);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@buildId", buildId);
        cmd.Parameters.AddWithValue("@repository", repository);
        cmd.Parameters.AddWithValue("@prNumber", (object?)prNumber ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Gets all pending triage requests.
    /// </summary>
    public List<MonitorTriageRequest> GetPendingTriageRequests()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT tr.id, tr.build_id, tr.repository, tr.pr_number, b.azdo_build_id
            FROM triage_requests tr
            JOIN builds b ON b.id = tr.build_id
            WHERE tr.status = 'pending'
            ORDER BY tr.created_at;
            """;

        var list = new List<MonitorTriageRequest>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new MonitorTriageRequest
            {
                Id = reader.GetInt64(0),
                BuildId = reader.GetInt64(1),
                Repository = reader.GetString(2),
                PrNumber = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                AzdoBuildId = reader.GetInt32(4),
            });
        }
        return list;
    }

    /// <summary>
    /// Marks a triage request as completed with its result JSON.
    /// </summary>
    public void CompleteTriageRequest(long id, string resultJson)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE triage_requests
            SET status = 'completed', result_json = @resultJson, completed_at = datetime('now')
            WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@resultJson", resultJson);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Marks a triage request as failed.
    /// </summary>
    public void FailTriageRequest(long id, string error)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE triage_requests
            SET status = 'failed', result_json = @error, completed_at = datetime('now')
            WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@error", error);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Saves the full AI chat log for a triage request.
    /// </summary>
    public void SaveTriageChatLog(long triageRequestId, string chatLogJson)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO triage_chat_logs (triage_request_id, chat_log)
            VALUES (@triageRequestId, @chatLog);
            """;
        cmd.Parameters.AddWithValue("@triageRequestId", triageRequestId);
        cmd.Parameters.AddWithValue("@chatLog", chatLogJson);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Gets the triage result and chat log for a build by AzDO build ID.
    /// Returns null if no completed triage exists.
    /// </summary>
    public MonitorTriageDetail? GetTriageDetailForBuild(int azdoBuildId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT tr.id, tr.status, tr.result_json, tr.created_at, tr.completed_at,
                   tcl.chat_log
            FROM triage_requests tr
            JOIN builds b ON b.id = tr.build_id
            LEFT JOIN triage_chat_logs tcl ON tcl.triage_request_id = tr.id
            WHERE b.azdo_build_id = @azdoBuildId
            ORDER BY tr.created_at DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@azdoBuildId", azdoBuildId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return new MonitorTriageDetail
        {
            TriageRequestId = reader.GetInt64(0),
            Status = reader.GetString(1),
            ResultJson = reader.IsDBNull(2) ? null : reader.GetString(2),
            CreatedAt = reader.GetString(3),
            CompletedAt = reader.IsDBNull(4) ? null : reader.GetString(4),
            ChatLog = reader.IsDBNull(5) ? null : reader.GetString(5),
        };
    }

    /// <summary>
    /// Records or updates a flaky test entry. Returns the flaky_tests row ID.
    /// </summary>
    public long UpsertFlakyTest(string testName, string repository, int? issueNumber, string? issueUrl, string? rationale = null)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO flaky_tests (test_name, repository, issue_number, issue_url, rationale)
            VALUES (@testName, @repository, @issueNumber, @issueUrl, @rationale)
            ON CONFLICT(test_name, repository) DO UPDATE SET
                last_seen = datetime('now'),
                occurrence_count = occurrence_count + 1,
                issue_number = COALESCE(@issueNumber, issue_number),
                issue_url = COALESCE(@issueUrl, issue_url),
                rationale = COALESCE(@rationale, rationale);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@testName", testName);
        cmd.Parameters.AddWithValue("@repository", repository);
        cmd.Parameters.AddWithValue("@issueNumber", (object?)issueNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@issueUrl", (object?)issueUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rationale", (object?)rationale ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Creates a fix request.
    /// </summary>
    public long InsertFixRequest(long triageRequestId, string repository, string testName, string diagnosis, string? proposedFix, string? issueUrl)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO fix_requests (triage_request_id, repository, test_name, diagnosis, proposed_fix, issue_url)
            VALUES (@triageRequestId, @repository, @testName, @diagnosis, @proposedFix, @issueUrl);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@triageRequestId", triageRequestId);
        cmd.Parameters.AddWithValue("@repository", repository);
        cmd.Parameters.AddWithValue("@testName", testName);
        cmd.Parameters.AddWithValue("@diagnosis", diagnosis);
        cmd.Parameters.AddWithValue("@proposedFix", (object?)proposedFix ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@issueUrl", (object?)issueUrl ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Gets all pending fix requests.
    /// </summary>
    public List<MonitorFixRequest> GetPendingFixRequests()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, triage_request_id, repository, test_name, diagnosis, proposed_fix, issue_url
            FROM fix_requests
            WHERE status = 'pending'
            ORDER BY created_at;
            """;

        var list = new List<MonitorFixRequest>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new MonitorFixRequest
            {
                Id = reader.GetInt64(0),
                TriageRequestId = reader.GetInt64(1),
                Repository = reader.GetString(2),
                TestName = reader.GetString(3),
                Diagnosis = reader.GetString(4),
                ProposedFix = reader.IsDBNull(5) ? null : reader.GetString(5),
                IssueUrl = reader.IsDBNull(6) ? null : reader.GetString(6),
            });
        }
        return list;
    }

    /// <summary>
    /// Marks a fix request as completed with the PR URL.
    /// </summary>
    public void CompleteFixRequest(long id, string? prUrl)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE fix_requests
            SET status = 'completed', pr_url = @prUrl, completed_at = datetime('now')
            WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@prUrl", (object?)prUrl ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Marks a fix request as failed.
    /// </summary>
    public void FailFixRequest(long id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE fix_requests
            SET status = 'failed', completed_at = datetime('now')
            WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Gets the distinct test names for failures in a build.
    /// </summary>
    public List<string> GetTestFailureNames(long buildId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT test_name FROM test_failures WHERE build_id = @buildId";
        cmd.Parameters.AddWithValue("@buildId", buildId);

        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }

    /// <summary>
    /// Gets builds that are ready for flaky analysis: AzDO data collected, has test failures, not yet triaged.
    /// </summary>
    public List<MonitorTriageTarget> GetBuildsForTriaging(int limit = 1)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT b.id, b.azdo_build_id, b.repository, b.source_branch, b.result, b.timeline_json
            FROM builds b
            WHERE b.triage_state = 'pending'
              AND b.azdo_failure_state = 'collected'
              AND b.helix_failure_state = 'collected'
              AND b.has_test_failures = 1
            ORDER BY b.created_at DESC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        var list = new List<MonitorTriageTarget>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new MonitorTriageTarget
            {
                BuildId = reader.GetInt64(0),
                AzdoBuildId = reader.GetInt32(1),
                Repository = reader.GetString(2),
                SourceBranch = reader.GetString(3),
                Result = reader.IsDBNull(4) ? null : reader.GetString(4),
                TimelineJson = reader.IsDBNull(5) ? null : reader.GetString(5),
            });
        }
        return list;
    }

    /// <summary>
    /// Sets the triage state for a build.
    /// </summary>
    public void SetTriageState(long buildId, string state)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE builds SET triage_state = @state WHERE id = @id";
        cmd.Parameters.AddWithValue("@state", state);
        cmd.Parameters.AddWithValue("@id", buildId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Gets the failure history for a test across all builds. Returns build IDs, results, and dates.
    /// </summary>
    public List<MonitorTestHistoryEntry> GetTestHistoryForName(string testName, string repository, int limit = 20)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT b.azdo_build_id, b.result, b.source_branch, b.finish_time, tf.outcome, tf.error_message
            FROM test_failures tf
            JOIN builds b ON b.id = tf.build_id
            WHERE tf.test_name = @testName AND b.repository = @repository
            ORDER BY b.created_at DESC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@testName", testName);
        cmd.Parameters.AddWithValue("@repository", repository);
        cmd.Parameters.AddWithValue("@limit", limit);

        var list = new List<MonitorTestHistoryEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new MonitorTestHistoryEntry
            {
                AzdoBuildId = reader.GetInt32(0),
                BuildResult = reader.IsDBNull(1) ? null : reader.GetString(1),
                SourceBranch = reader.GetString(2),
                FinishTime = reader.IsDBNull(3) ? null : reader.GetString(3),
                Outcome = reader.GetString(4),
                ErrorMessage = reader.IsDBNull(5) ? null : reader.GetString(5),
            });
        }
        return list;
    }

    /// <summary>
    /// Gets the flaky test record for a test name and repository, if one exists.
    /// </summary>
    public MonitorFlakyTestRecord? GetFlakyTest(string testName, string repository)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, test_name, repository, occurrence_count, issue_number, issue_url, first_seen, last_seen, rationale
            FROM flaky_tests
            WHERE test_name = @testName AND repository = @repository;
            """;
        cmd.Parameters.AddWithValue("@testName", testName);
        cmd.Parameters.AddWithValue("@repository", repository);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return new MonitorFlakyTestRecord
            {
                Id = reader.GetInt64(0),
                TestName = reader.GetString(1),
                Repository = reader.GetString(2),
                OccurrenceCount = reader.GetInt32(3),
                IssueNumber = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                IssueUrl = reader.IsDBNull(5) ? null : reader.GetString(5),
                FirstSeen = reader.GetString(6),
                LastSeen = reader.GetString(7),
                Rationale = reader.IsDBNull(8) ? null : reader.GetString(8),
            };
        }
        return null;
    }

    /// <summary>
    /// Gets all tracked flaky tests, ordered by most recently seen.
    /// </summary>
    public List<MonitorFlakyTestRecord> GetAllFlakyTests()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, test_name, repository, occurrence_count, issue_number, issue_url, first_seen, last_seen, rationale
            FROM flaky_tests
            ORDER BY last_seen DESC;
            """;

        var list = new List<MonitorFlakyTestRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new MonitorFlakyTestRecord
            {
                Id = reader.GetInt64(0),
                TestName = reader.GetString(1),
                Repository = reader.GetString(2),
                OccurrenceCount = reader.GetInt32(3),
                IssueNumber = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                IssueUrl = reader.IsDBNull(5) ? null : reader.GetString(5),
                FirstSeen = reader.GetString(6),
                LastSeen = reader.GetString(7),
                Rationale = reader.IsDBNull(8) ? null : reader.GetString(8),
            });
        }
        return list;
    }

    /// <summary>
    /// Gets recent triage results (completed triage requests) with build info.
    /// </summary>
    public List<MonitorTriageResultRecord> GetRecentTriageResults(int limit = 50)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT tr.id, tr.build_id, b.azdo_build_id, tr.repository, tr.result_json,
                   tr.created_at, tr.completed_at, b.source_branch
            FROM triage_requests tr
            JOIN builds b ON b.id = tr.build_id
            WHERE tr.status = 'completed'
            ORDER BY tr.completed_at DESC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        var list = new List<MonitorTriageResultRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new MonitorTriageResultRecord
            {
                Id = reader.GetInt64(0),
                BuildId = reader.GetInt64(1),
                AzdoBuildId = reader.GetInt32(2),
                Repository = reader.GetString(3),
                ResultJson = reader.IsDBNull(4) ? null : reader.GetString(4),
                CreatedAt = reader.GetString(5),
                CompletedAt = reader.IsDBNull(6) ? null : reader.GetString(6),
                SourceBranch = reader.GetString(7),
            });
        }
        return list;
    }

    /// <summary>
    /// Updates the issue tracking info for a flaky test.
    /// </summary>
    public void UpdateFlakyTestIssue(long id, int issueNumber, string issueUrl)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE flaky_tests SET issue_number = @issueNumber, issue_url = @issueUrl
            WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@issueNumber", issueNumber);
        cmd.Parameters.AddWithValue("@issueUrl", issueUrl);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Gets fix requests associated with a specific flaky test.
    /// </summary>
    public List<MonitorFixRequest> GetFixRequestsForTest(string testName, string repository)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, triage_request_id, repository, test_name, diagnosis, proposed_fix, issue_url
            FROM fix_requests
            WHERE test_name = @testName AND repository = @repository
            ORDER BY created_at DESC;
            """;
        cmd.Parameters.AddWithValue("@testName", testName);
        cmd.Parameters.AddWithValue("@repository", repository);

        var list = new List<MonitorFixRequest>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new MonitorFixRequest
            {
                Id = reader.GetInt64(0),
                TriageRequestId = reader.GetInt64(1),
                Repository = reader.GetString(2),
                TestName = reader.GetString(3),
                Diagnosis = reader.GetString(4),
                ProposedFix = reader.IsDBNull(5) ? null : reader.GetString(5),
                IssueUrl = reader.IsDBNull(6) ? null : reader.GetString(6),
            });
        }
        return list;
    }
    public List<MonitorBuildRecord> GetRecentBuilds(int limit = 50, bool failedOnly = false)
    {
        using var cmd = _connection.CreateCommand();
        var whereClause = failedOnly ? "WHERE b.result = 'failed'" : "";
        cmd.CommandText = $"""
            SELECT b.azdo_build_id, b.repository, b.build_number, b.source_branch,
                   b.definition_name, b.status, b.result, b.finish_time, b.has_test_failures,
                   b.azdo_failure_state, b.helix_failure_state,
                   (SELECT COUNT(*) FROM test_failures tf WHERE tf.build_id = b.id) as failure_count,
                   (SELECT COUNT(*) FROM helix_work_items h WHERE h.build_id = b.id) as helix_count
            FROM builds b
            {whereClause}
            ORDER BY b.created_at DESC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        var list = new List<MonitorBuildRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new MonitorBuildRecord
            {
                AzdoBuildId = reader.GetInt32(0),
                Repository = reader.GetString(1),
                BuildNumber = reader.GetString(2),
                SourceBranch = reader.GetString(3),
                DefinitionName = reader.GetString(4),
                Status = reader.GetString(5),
                Result = reader.IsDBNull(6) ? null : reader.GetString(6),
                FinishTime = reader.IsDBNull(7) ? null : reader.GetString(7),
                HasTestFailures = reader.IsDBNull(8) ? null : reader.GetInt32(8) != 0,
                AzdoFailureState = reader.GetString(9),
                HelixFailureState = reader.GetString(10),
                TestFailureCount = reader.GetInt32(11),
                HelixFailureCount = reader.GetInt32(12),
            });
        }
        return list;
    }

    /// <summary>
    /// Gets a single build record by its AzDO build ID, or null if not found.
    /// </summary>
    public MonitorBuildRecord? GetBuildByAzdoId(int azdoBuildId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT b.azdo_build_id, b.repository, b.build_number, b.source_branch,
                   b.definition_name, b.status, b.result, b.finish_time, b.has_test_failures,
                   b.azdo_failure_state, b.helix_failure_state,
                   (SELECT COUNT(*) FROM test_failures tf WHERE tf.build_id = b.id) as failure_count,
                   (SELECT COUNT(*) FROM helix_work_items h WHERE h.build_id = b.id) as helix_count
            FROM builds b
            WHERE b.azdo_build_id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", azdoBuildId);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return new MonitorBuildRecord
            {
                AzdoBuildId = reader.GetInt32(0),
                Repository = reader.GetString(1),
                BuildNumber = reader.GetString(2),
                SourceBranch = reader.GetString(3),
                DefinitionName = reader.GetString(4),
                Status = reader.GetString(5),
                Result = reader.IsDBNull(6) ? null : reader.GetString(6),
                FinishTime = reader.IsDBNull(7) ? null : reader.GetString(7),
                HasTestFailures = reader.IsDBNull(8) ? null : reader.GetInt32(8) != 0,
                AzdoFailureState = reader.GetString(9),
                HelixFailureState = reader.GetString(10),
                TestFailureCount = reader.GetInt32(11),
                HelixFailureCount = reader.GetInt32(12),
            };
        }
        return null;
    }

    /// <summary>
    /// Gets builds that need failure data collection (state = pending, not recently attempted).
    /// </summary>
    public List<MonitorCollectionTarget> GetPendingCollectionTargets(int limit = 4)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, azdo_build_id, repository, azdo_failure_state, helix_failure_state,
                   has_test_failures, finish_time
            FROM builds
            WHERE (azdo_failure_state = 'pending' OR helix_failure_state = 'pending')
              AND (last_collection_attempt IS NULL 
                   OR datetime(last_collection_attempt, '+30 seconds') < datetime('now'))
            ORDER BY created_at DESC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        var list = new List<MonitorCollectionTarget>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new MonitorCollectionTarget
            {
                BuildId = reader.GetInt64(0),
                AzdoBuildId = reader.GetInt32(1),
                Repository = reader.GetString(2),
                AzdoFailureState = reader.GetString(3),
                HelixFailureState = reader.GetString(4),
                HasTestFailures = reader.IsDBNull(5) ? null : reader.GetInt32(5) != 0,
                FinishTime = reader.IsDBNull(6) ? null : reader.GetString(6),
            });
        }
        return list;
    }

    /// <summary>
    /// Marks AzDO test failure collection as successful for a build.
    /// </summary>
    public void SetAzdoCollected(long buildId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE builds SET azdo_failure_state = 'collected', azdo_collection_state = 0
            WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", buildId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Marks Helix work item collection as successful for a build.
    /// </summary>
    public void SetHelixCollected(long buildId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE builds SET helix_failure_state = 'collected', helix_collection_state = 0
            WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", buildId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Updates the console summary for a Helix work item.
    /// </summary>
    public void UpdateHelixConsoleSummary(long helixWorkItemId, string summary)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE helix_work_items SET console_summary = @summary WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", helixWorkItemId);
        cmd.Parameters.AddWithValue("@summary", summary);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Records an AzDO collection failure. If consecutive failures reach the max, marks
    /// azdo_failure_state as 'failed'.
    /// </summary>
    public void RecordAzdoCollectionFailure(long buildId, int maxFailures = 3)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE builds SET
                azdo_collection_state = azdo_collection_state + 1,
                last_collection_attempt = datetime('now'),
                azdo_failure_state = CASE 
                    WHEN azdo_failure_state = 'pending' AND azdo_collection_state + 1 >= @max THEN 'failed'
                    ELSE azdo_failure_state END
            WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", buildId);
        cmd.Parameters.AddWithValue("@max", maxFailures);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Records a Helix collection failure. If consecutive failures reach the max, marks
    /// helix_failure_state as 'failed'.
    /// </summary>
    public void RecordHelixCollectionFailure(long buildId, int maxFailures = 3)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE builds SET
                helix_collection_state = helix_collection_state + 1,
                last_collection_attempt = datetime('now'),
                helix_failure_state = CASE 
                    WHEN helix_failure_state = 'pending' AND helix_collection_state + 1 >= @max THEN 'failed'
                    ELSE helix_failure_state END
            WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", buildId);
        cmd.Parameters.AddWithValue("@max", maxFailures);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Resets a build's collection state from 'failed' back to 'pending' for retry.
    /// </summary>
    public bool ResetCollectionState(int azdoBuildId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE builds SET
                azdo_failure_state = CASE WHEN azdo_failure_state = 'failed' THEN 'pending' ELSE azdo_failure_state END,
                helix_failure_state = CASE WHEN helix_failure_state = 'failed' THEN 'pending' ELSE helix_failure_state END,
                azdo_collection_state = CASE WHEN azdo_failure_state = 'failed' THEN 0 ELSE azdo_collection_state END,
                helix_collection_state = CASE WHEN helix_failure_state = 'failed' THEN 0 ELSE helix_collection_state END,
                last_collection_attempt = NULL
            WHERE azdo_build_id = @id
              AND (azdo_failure_state = 'failed' OR helix_failure_state = 'failed');
            """;
        cmd.Parameters.AddWithValue("@id", azdoBuildId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Fully resets collection data for a build: deletes all test failures and helix work items,
    /// resets both collection states to 'pending', and clears the last collection attempt.
    /// The collection jobs will re-collect the data on their next pass.
    /// </summary>
    public bool ResetBuildCollectionData(int azdoBuildId)
    {
        var rowId = GetBuildRowId(azdoBuildId);
        if (rowId is null)
            return false;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM helix_work_items WHERE build_id = @id";
        cmd.Parameters.AddWithValue("@id", rowId.Value);
        cmd.ExecuteNonQuery();

        using var cmd2 = _connection.CreateCommand();
        cmd2.CommandText = "DELETE FROM test_failures WHERE build_id = @id";
        cmd2.Parameters.AddWithValue("@id", rowId.Value);
        cmd2.ExecuteNonQuery();

        using var cmd3 = _connection.CreateCommand();
        cmd3.CommandText = """
            UPDATE builds SET
                azdo_failure_state = 'pending',
                helix_failure_state = 'pending',
                collection_failures = 0,
                last_collection_attempt = NULL
            WHERE id = @id;
            """;
        cmd3.Parameters.AddWithValue("@id", rowId.Value);
        cmd3.ExecuteNonQuery();

        return true;
    }

    /// <summary>
    /// Inserts a Helix work item record.
    /// </summary>
    public long InsertHelixWorkItem(long buildId, string friendlyName, int exitCode, string machineName,
        string queueName, string consoleUri, long jobId, long workItemId, string status)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO helix_work_items (build_id, friendly_name, exit_code, machine_name, queue_name, console_uri, job_id, work_item_id, status)
            VALUES (@buildId, @friendlyName, @exitCode, @machineName, @queueName, @consoleUri, @jobId, @workItemId, @status);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@buildId", buildId);
        cmd.Parameters.AddWithValue("@friendlyName", friendlyName);
        cmd.Parameters.AddWithValue("@exitCode", exitCode);
        cmd.Parameters.AddWithValue("@machineName", machineName);
        cmd.Parameters.AddWithValue("@queueName", queueName);
        cmd.Parameters.AddWithValue("@consoleUri", consoleUri);
        cmd.Parameters.AddWithValue("@jobId", jobId);
        cmd.Parameters.AddWithValue("@workItemId", workItemId);
        cmd.Parameters.AddWithValue("@status", status);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Gets test failures for a build by its AzDO build ID.
    /// </summary>
    public List<MonitorTestFailureRecord> GetTestFailuresForBuild(int azdoBuildId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT tf.test_name, tf.outcome, tf.error_message, tf.stack_trace
            FROM test_failures tf
            JOIN builds b ON b.id = tf.build_id
            WHERE b.azdo_build_id = @id
            ORDER BY tf.test_name;
            """;
        cmd.Parameters.AddWithValue("@id", azdoBuildId);

        var list = new List<MonitorTestFailureRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new MonitorTestFailureRecord
            {
                TestName = reader.GetString(0),
                Outcome = reader.GetString(1),
                ErrorMessage = reader.IsDBNull(2) ? null : reader.GetString(2),
                StackTrace = reader.IsDBNull(3) ? null : reader.GetString(3),
            });
        }
        return list;
    }

    /// <summary>
    /// Gets helix work items for a build by its AzDO build ID.
    /// </summary>
    public List<MonitorHelixWorkItemRecord> GetHelixWorkItemsForBuild(int azdoBuildId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT h.friendly_name, h.exit_code, h.machine_name, h.queue_name,
                   h.console_uri, h.job_id, h.work_item_id, h.status, h.console_summary
            FROM helix_work_items h
            JOIN builds b ON b.id = h.build_id
            WHERE b.azdo_build_id = @id
            ORDER BY h.friendly_name;
            """;
        cmd.Parameters.AddWithValue("@id", azdoBuildId);

        var list = new List<MonitorHelixWorkItemRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new MonitorHelixWorkItemRecord
            {
                FriendlyName = reader.GetString(0),
                ExitCode = reader.GetInt32(1),
                MachineName = reader.GetString(2),
                QueueName = reader.GetString(3),
                ConsoleUri = reader.GetString(4),
                JobId = reader.GetInt64(5),
                WorkItemId = reader.GetInt64(6),
                Status = reader.GetString(7),
                ConsoleSummary = reader.IsDBNull(8) ? null : reader.GetString(8),
            });
        }
        return list;
    }

    /// <summary>
    /// Searches for test failures whose name contains the given partial match string.
    /// Returns distinct test names, the build they failed in, and the outcome.
    /// </summary>
    public List<MonitorTestSearchResult> SearchTestFailures(string namePattern, int limit = 50)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT tf.test_name, b.azdo_build_id, b.repository, tf.outcome, b.finish_time
            FROM test_failures tf
            JOIN builds b ON b.id = tf.build_id
            WHERE tf.test_name LIKE @pattern
            ORDER BY b.finish_time DESC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@pattern", $"%{namePattern}%");
        cmd.Parameters.AddWithValue("@limit", limit);

        var list = new List<MonitorTestSearchResult>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new MonitorTestSearchResult
            {
                TestName = reader.GetString(0),
                AzdoBuildId = reader.GetInt32(1),
                Repository = reader.GetString(2),
                Outcome = reader.GetString(3),
                FinishTime = reader.IsDBNull(4) ? null : reader.GetString(4),
            });
        }
        return list;
    }

    public void Dispose() => _connection.Dispose();
}

public sealed class MonitorTriageRequest
{
    public required long Id { get; init; }
    public required long BuildId { get; init; }
    public required string Repository { get; init; }
    public int? PrNumber { get; init; }
    public required int AzdoBuildId { get; init; }
}

public sealed class MonitorFixRequest
{
    public required long Id { get; init; }
    public required long TriageRequestId { get; init; }
    public required string Repository { get; init; }
    public required string TestName { get; init; }
    public required string Diagnosis { get; init; }
    public string? ProposedFix { get; init; }
    public string? IssueUrl { get; init; }
}

public sealed class MonitorBuildRecord
{
    public required int AzdoBuildId { get; init; }
    public required string Repository { get; init; }
    public required string BuildNumber { get; init; }
    public required string SourceBranch { get; init; }
    public required string DefinitionName { get; init; }
    public required string Status { get; init; }
    public string? Result { get; init; }
    public string? FinishTime { get; init; }
    public bool? HasTestFailures { get; init; }
    public required string AzdoFailureState { get; init; }
    public required string HelixFailureState { get; init; }
    public int TestFailureCount { get; init; }
    public int HelixFailureCount { get; init; }
}

public sealed class MonitorCollectionTarget
{
    public required long BuildId { get; init; }
    public required int AzdoBuildId { get; init; }
    public required string Repository { get; init; }
    public required string AzdoFailureState { get; init; }
    public required string HelixFailureState { get; init; }
    public bool? HasTestFailures { get; init; }
    public string? FinishTime { get; init; }
}

public sealed class MonitorTestFailureRecord
{
    public required string TestName { get; init; }
    public required string Outcome { get; init; }
    public string? ErrorMessage { get; init; }
    public string? StackTrace { get; init; }
}

public sealed class MonitorHelixWorkItemRecord
{
    public required string FriendlyName { get; init; }
    public required int ExitCode { get; init; }
    public required string MachineName { get; init; }
    public required string QueueName { get; init; }
    public required string ConsoleUri { get; init; }
    public required long JobId { get; init; }
    public required long WorkItemId { get; init; }
    public required string Status { get; init; }
    public string? ConsoleSummary { get; init; }
}

public sealed class MonitorTriageTarget
{
    public required long BuildId { get; init; }
    public required int AzdoBuildId { get; init; }
    public required string Repository { get; init; }
    public required string SourceBranch { get; init; }
    public string? Result { get; init; }
    public string? TimelineJson { get; init; }
}

public sealed class MonitorTestHistoryEntry
{
    public required int AzdoBuildId { get; init; }
    public string? BuildResult { get; init; }
    public required string SourceBranch { get; init; }
    public string? FinishTime { get; init; }
    public required string Outcome { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class MonitorFlakyTestRecord
{
    public required long Id { get; init; }
    public required string TestName { get; init; }
    public required string Repository { get; init; }
    public required int OccurrenceCount { get; init; }
    public int? IssueNumber { get; init; }
    public string? IssueUrl { get; init; }
    public string? Rationale { get; init; }
    public required string FirstSeen { get; init; }
    public required string LastSeen { get; init; }
}

public sealed class MonitorTriageResultRecord
{
    public required long Id { get; init; }
    public required long BuildId { get; init; }
    public required int AzdoBuildId { get; init; }
    public required string Repository { get; init; }
    public string? ResultJson { get; init; }
    public required string CreatedAt { get; init; }
    public string? CompletedAt { get; init; }
    public required string SourceBranch { get; init; }
}

public sealed class MonitorTriageDetail
{
    public required long TriageRequestId { get; init; }
    public required string Status { get; init; }
    public string? ResultJson { get; init; }
    public required string CreatedAt { get; init; }
    public string? CompletedAt { get; init; }
    public string? ChatLog { get; init; }
}

/// <summary>
/// Summary of AzDO build timeline data stored as a JSON blob on the build record.
/// Contains only diagnostic-relevant information: failed jobs and error/warning issues.
/// </summary>
public sealed class MonitorTimelineSummary
{
    [JsonPropertyName("failedJobs")]
    public List<MonitorTimelineJobEntry> FailedJobs { get; init; } = [];

    [JsonPropertyName("issues")]
    public List<MonitorTimelineIssueEntry> Issues { get; init; } = [];
}

public sealed class MonitorTimelineJobEntry
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("result")]
    public required string Result { get; init; }

    [JsonPropertyName("workerName")]
    public string? WorkerName { get; init; }
}

public sealed class MonitorTimelineIssueEntry
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("category")]
    public string? Category { get; init; }
}

public sealed class MonitorTestSearchResult
{
    public required string TestName { get; init; }
    public required int AzdoBuildId { get; init; }
    public required string Repository { get; init; }
    public required string Outcome { get; init; }
    public string? FinishTime { get; init; }
}
