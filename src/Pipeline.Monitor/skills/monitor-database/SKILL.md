---
name: monitor-database
description: Schema and usage guide for the Pipeline Monitor SQLite database that tracks builds, test failures, triage, and fixes
---

# Monitor Database

The Pipeline Monitor service uses a SQLite database to track AzDO builds, test failures, flaky test history, and triage/fix workflow state. The database file path is configured in `monitor-config.json` (default: `monitor.db`).

The schema is defined in `src/Pipeline.Monitor/MonitorDatabase.cs` and auto-created on startup.

## Tables

### `builds`

Records every completed AzDO build the monitor has processed. Used to avoid reprocessing.

| Column | Type | Description |
|--------|------|-------------|
| `id` | INTEGER PK | Internal row ID |
| `azdo_build_id` | INTEGER UNIQUE | AzDO build ID (e.g., 1379081) |
| `repository` | TEXT | GitHub repo in `owner/repo` format (e.g., `dotnet/roslyn`) |
| `build_number` | TEXT | AzDO build number string |
| `source_branch` | TEXT | Git ref (e.g., `refs/pull/12345/merge` for PR builds) |
| `definition_name` | TEXT | Pipeline definition name |
| `status` | TEXT | Build status (e.g., `completed`) |
| `result` | TEXT | Build result (e.g., `succeeded`, `failed`, `partiallySucceeded`) |
| `finish_time` | TEXT | ISO 8601 timestamp |
| `has_test_failures` | INTEGER | 1 if the build had test failures, 0 if not, NULL if not yet determined |
| `created_at` | TEXT | When this record was created |

### `test_failures`

Individual test failure records linked to builds.

| Column | Type | Description |
|--------|------|-------------|
| `id` | INTEGER PK | Internal row ID |
| `build_id` | INTEGER FK → builds(id) | The build this failure belongs to |
| `test_name` | TEXT | Fully qualified test name (from AzDO `testCaseTitle`) |
| `outcome` | TEXT | Test outcome (e.g., `Failed`, `Aborted`) |
| `error_message` | TEXT | Error message from the test failure (nullable) |
| `stack_trace` | TEXT | Stack trace from the test failure (nullable) |
| `created_at` | TEXT | When this record was created |

### `triage_requests`

Work queue for the triage agent. One request per build with test failures.

| Column | Type | Description |
|--------|------|-------------|
| `id` | INTEGER PK | Internal row ID |
| `build_id` | INTEGER FK → builds(id) | The build to triage |
| `repository` | TEXT | GitHub repo in `owner/repo` format |
| `pr_number` | INTEGER | PR number extracted from source branch (nullable for CI builds) |
| `status` | TEXT | `pending`, `completed`, or `failed` |
| `result_json` | TEXT | Structured JSON output from the triage agent (nullable) |
| `created_at` | TEXT | When the request was created |
| `completed_at` | TEXT | When the request was processed |

The `result_json` field contains the triage agent's structured output with `flakyTests`, `realFailures`, and `prCommented` fields. See the `flaky-test-triage` agent for the full JSON schema.

### `flaky_tests`

Running inventory of confirmed flaky tests with occurrence tracking and linked GitHub issues.

| Column | Type | Description |
|--------|------|-------------|
| `id` | INTEGER PK | Internal row ID |
| `test_name` | TEXT | Fully qualified test name |
| `repository` | TEXT | GitHub repo in `owner/repo` format |
| `first_seen` | TEXT | When this test was first identified as flaky |
| `last_seen` | TEXT | Most recent flaky occurrence |
| `occurrence_count` | INTEGER | How many times this test has been flagged as flaky |
| `issue_number` | INTEGER | GitHub issue number tracking this flaky test (nullable) |
| `issue_url` | TEXT | Full URL to the GitHub issue (nullable) |

Unique constraint on `(test_name, repository)` — each test has one row per repo. New occurrences increment `occurrence_count` and update `last_seen`.

### `fix_requests`

Work queue for the fix agent. Created when the triage agent identifies a fixable flaky test.

| Column | Type | Description |
|--------|------|-------------|
| `id` | INTEGER PK | Internal row ID |
| `triage_request_id` | INTEGER FK → triage_requests(id) | The triage request that spawned this |
| `repository` | TEXT | GitHub repo in `owner/repo` format |
| `test_name` | TEXT | Fully qualified test name to fix |
| `diagnosis` | TEXT | Root cause diagnosis from the triage agent |
| `proposed_fix` | TEXT | Suggested fix approach (nullable) |
| `issue_url` | TEXT | GitHub issue URL to reference in the PR (nullable) |
| `status` | TEXT | `pending`, `completed`, or `failed` |
| `pr_url` | TEXT | URL of the submitted fix PR (nullable, set on completion) |
| `created_at` | TEXT | When the request was created |
| `completed_at` | TEXT | When the request was processed |

## Data Flow

```
AzDO build completes
  → BuildPollingService records in `builds`
  → If failures: records in `test_failures`, creates `triage_requests` (status=pending)

TriageService picks up pending triage_requests
  → Invokes flaky-test-triage agent
  → Updates `triage_requests` (status=completed, result_json set)
  → Upserts `flaky_tests` for confirmed flaky tests
  → Creates `fix_requests` (status=pending) for fixable tests

TriageService picks up pending fix_requests
  → Invokes flaky-test-fix agent
  → Updates `fix_requests` (status=completed, pr_url set)
```

## Key Queries

Find all flaky tests for a repository, ordered by frequency:
```sql
SELECT test_name, occurrence_count, last_seen, issue_url
FROM flaky_tests
WHERE repository = 'dotnet/roslyn'
ORDER BY occurrence_count DESC;
```

Find builds with unprocessed failures:
```sql
SELECT b.azdo_build_id, b.build_number, b.source_branch, tr.status
FROM triage_requests tr
JOIN builds b ON b.id = tr.build_id
WHERE tr.status = 'pending';
```

Find all test failures for a specific test across builds:
```sql
SELECT b.azdo_build_id, b.build_number, b.finish_time, tf.error_message
FROM test_failures tf
JOIN builds b ON b.id = tf.build_id
WHERE tf.test_name = 'Some.Test.Name'
ORDER BY b.finish_time DESC;
```
