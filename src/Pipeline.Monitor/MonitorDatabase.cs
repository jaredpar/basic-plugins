using Microsoft.Data.Sqlite;

namespace Pipeline.Monitor;

/// <summary>
/// SQLite data access layer for the monitor service. Tracks builds, test failures,
/// triage requests, and filed issues.
/// </summary>
public sealed class MonitorDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    private MonitorDatabase(SqliteConnection connection)
    {
        _connection = connection;
    }

    public static MonitorDatabase Open(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        var db = new MonitorDatabase(connection);
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
                has_test_failures INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS test_failures (
                id INTEGER PRIMARY KEY,
                build_id INTEGER NOT NULL REFERENCES builds(id),
                test_name TEXT NOT NULL,
                outcome TEXT NOT NULL,
                error_message TEXT,
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

            CREATE INDEX IF NOT EXISTS idx_builds_azdo ON builds(azdo_build_id);
            CREATE INDEX IF NOT EXISTS idx_builds_repo ON builds(repository);
            CREATE INDEX IF NOT EXISTS idx_test_failures_build ON test_failures(build_id);
            CREATE INDEX IF NOT EXISTS idx_triage_requests_status ON triage_requests(status);
            CREATE INDEX IF NOT EXISTS idx_fix_requests_status ON fix_requests(status);
            CREATE INDEX IF NOT EXISTS idx_flaky_tests_name ON flaky_tests(test_name, repository);
            """;
        cmd.ExecuteNonQuery();
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
        bool hasTestFailures)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO builds (azdo_build_id, repository, build_number, source_branch, definition_name, status, result, finish_time, has_test_failures)
            VALUES (@azdoBuildId, @repository, @buildNumber, @sourceBranch, @definitionName, @status, @result, @finishTime, @hasTestFailures);
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
        cmd.Parameters.AddWithValue("@hasTestFailures", hasTestFailures ? 1 : 0);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Records a test failure linked to a build.
    /// </summary>
    public void InsertTestFailure(long buildId, string testName, string outcome, string? errorMessage)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO test_failures (build_id, test_name, outcome, error_message)
            VALUES (@buildId, @testName, @outcome, @errorMessage);
            """;
        cmd.Parameters.AddWithValue("@buildId", buildId);
        cmd.Parameters.AddWithValue("@testName", testName);
        cmd.Parameters.AddWithValue("@outcome", outcome);
        cmd.Parameters.AddWithValue("@errorMessage", (object?)errorMessage ?? DBNull.Value);
    }

    /// <summary>
    /// Creates a triage request for a build with test failures.
    /// </summary>
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
    public List<TriageRequest> GetPendingTriageRequests()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT tr.id, tr.build_id, tr.repository, tr.pr_number, b.azdo_build_id
            FROM triage_requests tr
            JOIN builds b ON b.id = tr.build_id
            WHERE tr.status = 'pending'
            ORDER BY tr.created_at;
            """;

        var list = new List<TriageRequest>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new TriageRequest
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
    /// Records or updates a flaky test entry. Returns the flaky_tests row ID.
    /// </summary>
    public long UpsertFlakyTest(string testName, string repository, int? issueNumber, string? issueUrl)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO flaky_tests (test_name, repository, issue_number, issue_url)
            VALUES (@testName, @repository, @issueNumber, @issueUrl)
            ON CONFLICT(test_name, repository) DO UPDATE SET
                last_seen = datetime('now'),
                occurrence_count = occurrence_count + 1,
                issue_number = COALESCE(@issueNumber, issue_number),
                issue_url = COALESCE(@issueUrl, issue_url);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@testName", testName);
        cmd.Parameters.AddWithValue("@repository", repository);
        cmd.Parameters.AddWithValue("@issueNumber", (object?)issueNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@issueUrl", (object?)issueUrl ?? DBNull.Value);
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
    public List<FixRequest> GetPendingFixRequests()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, triage_request_id, repository, test_name, diagnosis, proposed_fix, issue_url
            FROM fix_requests
            WHERE status = 'pending'
            ORDER BY created_at;
            """;

        var list = new List<FixRequest>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new FixRequest
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
    /// Gets recent builds with their test failure counts.
    /// </summary>
    public List<BuildRecord> GetRecentBuilds(int limit = 50)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT b.azdo_build_id, b.repository, b.build_number, b.source_branch,
                   b.definition_name, b.status, b.result, b.finish_time, b.has_test_failures,
                   COUNT(tf.id) as failure_count
            FROM builds b
            LEFT JOIN test_failures tf ON tf.build_id = b.id
            GROUP BY b.id
            ORDER BY b.created_at DESC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        var list = new List<BuildRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new BuildRecord
            {
                AzdoBuildId = reader.GetInt32(0),
                Repository = reader.GetString(1),
                BuildNumber = reader.GetString(2),
                SourceBranch = reader.GetString(3),
                DefinitionName = reader.GetString(4),
                Status = reader.GetString(5),
                Result = reader.IsDBNull(6) ? null : reader.GetString(6),
                FinishTime = reader.IsDBNull(7) ? null : reader.GetString(7),
                HasTestFailures = reader.GetInt32(8) != 0,
                TestFailureCount = reader.GetInt32(9),
            });
        }
        return list;
    }

    public void Dispose() => _connection.Dispose();
}

public sealed class TriageRequest
{
    public required long Id { get; init; }
    public required long BuildId { get; init; }
    public required string Repository { get; init; }
    public int? PrNumber { get; init; }
    public required int AzdoBuildId { get; init; }
}

public sealed class FixRequest
{
    public required long Id { get; init; }
    public required long TriageRequestId { get; init; }
    public required string Repository { get; init; }
    public required string TestName { get; init; }
    public required string Diagnosis { get; init; }
    public string? ProposedFix { get; init; }
    public string? IssueUrl { get; init; }
}

public sealed class BuildRecord
{
    public required int AzdoBuildId { get; init; }
    public required string Repository { get; init; }
    public required string BuildNumber { get; init; }
    public required string SourceBranch { get; init; }
    public required string DefinitionName { get; init; }
    public required string Status { get; init; }
    public string? Result { get; init; }
    public string? FinishTime { get; init; }
    public bool HasTestFailures { get; init; }
    public int TestFailureCount { get; init; }
}
