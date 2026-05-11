using System.Text.Json;
using Pipeline.Core;

namespace Pipeline.Monitor;

/// <summary>
/// Background job that scans for builds with pending failure data collection,
/// fetches AzDO test failures and Helix work items, and records them in the database.
/// </summary>
public sealed class FailureCollectionJob
{
    private readonly MonitorDatabase _db;
    private readonly AzdoClient _azdoClient;
    private readonly HelixClient _helixClient;
    private readonly MonitorLog _log;
    private readonly SemaphoreSlim _semaphore = new(4);
    private readonly CancellationTokenSource _cts = new();
    private const string Source = "collector";

    public FailureCollectionJob(MonitorDatabase db, AzdoClient azdoClient, HelixClient helixClient, MonitorLog log)
    {
        _db = db;
        _azdoClient = azdoClient;
        _helixClient = helixClient;
        _log = log;
    }

    /// <summary>
    /// Starts the background collection loop.
    /// </summary>
    public Task StartAsync()
    {
        return Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public void Stop() => _cts.Cancel();

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        _log.Info(Source, "Failure collection job started");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var targets = _db.GetPendingCollectionTargets(limit: 4);

                if (targets.Count > 0)
                {
                    _log.Info(Source, $"Processing {targets.Count} build(s) with pending collection");
                    var tasks = targets.Select(t => ProcessTargetAsync(t, cancellationToken));
                    await Task.WhenAll(tasks);
                }
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error(Source, $"Unexpected error in collection loop: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _log.Info(Source, "Failure collection job stopped");
    }

    private async Task ProcessTargetAsync(CollectionTarget target, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (target.AzdoFailureState == "pending")
            {
                if (!await CollectAzdoTestFailuresAsync(target))
                    _db.RecordAzdoCollectionFailure(target.BuildId);
            }

            if (target.HelixFailureState == "pending")
            {
                var result = await CollectHelixWorkItemsAsync(target);
                if (result == HelixCollectionResult.Failed)
                    _db.RecordHelixCollectionFailure(target.BuildId);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Fetches AzDO test failures and timeline data for a build. Returns true on success.
    /// </summary>
    private async Task<bool> CollectAzdoTestFailuresAsync(CollectionTarget target)
    {
        try
        {
            // Collect test failures
            var failures = await _azdoClient.GetTestFailuresAsync(target.AzdoBuildId);

            foreach (var f in failures)
            {
                _db.InsertTestFailure(target.BuildId, f.TestCaseTitle, f.Outcome, f.ErrorMessage, f.StackTrace, f.Comment);
            }

            if (failures.Count > 0)
            {
                // Helix creates synthetic test results with HelixJobId in the comment field.
                // These are metadata markers, not real test failures — exclude them when
                // determining whether the build has genuine test failures.
                var realFailureCount = failures.Count(f =>
                    string.IsNullOrEmpty(f.Comment) || !f.Comment.Contains("HelixJobId"));

                _db.UpdateHasTestFailures(target.BuildId, realFailureCount > 0);
                _log.Info(Source, $"Build {target.AzdoBuildId}: collected {failures.Count} AzDO test result(s) ({realFailureCount} real failure(s), {failures.Count - realFailureCount} Helix marker(s))");
            }
            else
            {
                _log.Info(Source, $"Build {target.AzdoBuildId}: no AzDO test failures");
            }

            // Collect timeline data (failed jobs + error/warning issues)
            try
            {
                var timeline = await _azdoClient.GetTimelineAsync(target.AzdoBuildId);

                var failedJobs = timeline.Records
                    .Where(r => r.RecordType == "Job" && r.Result is not null and not "succeeded")
                    .Select(r => new TimelineJobEntry
                    {
                        Name = r.Name,
                        Result = r.Result!,
                        WorkerName = r.WorkerName,
                    })
                    .ToList();

                var issues = timeline.Records
                    .SelectMany(r => r.Issues)
                    .Where(i => i.Type is "error" or "warning")
                    .Select(i => new TimelineIssueEntry
                    {
                        Type = i.Type,
                        Message = i.Message,
                        Category = i.Category,
                    })
                    .ToList();

                var summary = new TimelineSummary
                {
                    FailedJobs = failedJobs,
                    Issues = issues,
                };

                _db.SetTimelineData(target.BuildId, JsonSerializer.Serialize(summary));

                if (failedJobs.Count > 0 || issues.Count > 0)
                {
                    _log.Info(Source, $"Build {target.AzdoBuildId}: timeline — {failedJobs.Count} failed job(s), {issues.Count} issue(s)");
                }
            }
            catch (Exception ex)
            {
                // Timeline is supplementary — log but don't fail the whole AzDO collection
                _log.Warn(Source, $"Build {target.AzdoBuildId}: timeline collection failed — {ex.Message}");
            }

            _db.SetAzdoCollected(target.BuildId);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn(Source, $"Build {target.AzdoBuildId}: AzDO collection failed — {ex.Message}");
            return false;
        }
    }

    private enum HelixCollectionResult
    {
        /// <summary>Collection succeeded — data stored and state marked collected.</summary>
        Success,
        /// <summary>Collection failed due to an error (network, Kusto, etc.).</summary>
        Failed,
    }

    /// <summary>
    /// Fetches Helix work items for a build using job names from AzDO test result comments.
    /// If no Helix comments exist, the build didn't use Helix and collection is skipped.
    /// </summary>
    private async Task<HelixCollectionResult> CollectHelixWorkItemsAsync(CollectionTarget target)
    {
        try
        {
            // The primary path: extract Helix job names from AzDO test result comments.
            // Helix embeds JSON with "HelixJobId" (actually the job name) in test result comments.
            var jobNames = _db.GetHelixJobNamesFromComments(target.BuildId);

            if (jobNames.Count == 0)
            {
                // No Helix comments means the build didn't run tests through Helix.
                // Nothing to collect.
                _log.Info(Source, $"Build {target.AzdoBuildId}: no Helix comments in test results, skipping Helix collection");
                _db.SetHelixCollected(target.BuildId);
                return HelixCollectionResult.Success;
            }

            _log.Info(Source, $"Build {target.AzdoBuildId}: querying Helix with {jobNames.Count} job name(s) from test result comments");
            var items = await _helixClient.GetHelixWorkItemsByJobNamesAsync(jobNames, target.AzdoBuildId);

            foreach (var item in items)
            {
                var rowId = _db.InsertHelixWorkItem(
                    target.BuildId,
                    item.FriendlyName,
                    item.ExitCode,
                    item.MachineName,
                    item.QueueName,
                    item.ConsoleUri,
                    item.JobId,
                    item.WorkItemId,
                    item.Status);

                // Fetch console output and extract error summary
                try
                {
                    using var httpClient = new HttpClient();
                    httpClient.Timeout = TimeSpan.FromSeconds(30);
                    var consoleText = await httpClient.GetStringAsync(item.ConsoleUri);
                    var summary = HelixConsoleExtractor.ExtractSummary(consoleText);

                    if (summary.Length > 0)
                    {
                        _db.UpdateHelixConsoleSummary(rowId, summary);
                    }
                }
                catch (Exception ex)
                {
                    // Console fetch is supplementary — log but don't fail the collection
                    _log.Warn(Source, $"Build {target.AzdoBuildId}: console fetch failed for {item.FriendlyName} — {ex.Message}");
                }
            }

            if (items.Count > 0)
            {
                _log.Info(Source, $"Build {target.AzdoBuildId}: collected {items.Count} failed Helix work item(s)");
            }
            else
            {
                _log.Info(Source, $"Build {target.AzdoBuildId}: no failed Helix work items");
            }

            _db.SetHelixCollected(target.BuildId);
            return HelixCollectionResult.Success;
        }
        catch (Exception ex)
        {
            _log.Warn(Source, $"Build {target.AzdoBuildId}: Helix collection failed — {ex.Message}");
            return HelixCollectionResult.Failed;
        }
    }
}
