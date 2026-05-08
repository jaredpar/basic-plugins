---
name: flaky-test-analysis
description: Domain knowledge for identifying and triaging flaky tests in .NET CI pipelines
---

# Flaky Test Analysis

This skill provides domain knowledge for analyzing flaky tests in .NET repositories that use AzDO pipelines and Helix for test execution.

## What Makes a Test Flaky

A flaky test is one that produces different results (pass/fail) on the same code without any changes. Flaky tests erode developer trust in CI and waste time investigating false failures.

## AzDO and Helix Test Execution Model

Understanding the test execution pipeline is critical for flaky test analysis:

1. A PR or CI build triggers an **AzDO pipeline**
2. The pipeline creates multiple **AzDO jobs** (e.g., `Windows_Desktop_UnitTests`, `Linux_UT`)
3. Each test job sends work to **Helix**, which distributes test execution across machines
4. Helix creates **work items** — each work item runs a subset of tests on a single machine
5. Results flow back: Helix → AzDO test results
6. If a job fails, AzDO may **retry** it (recorded as `AzdoAttempt` 2, 3, etc.)

### Key data relationships:
- `azdo_test_failures` gives you test-level results (name, outcome, error message) from AzDO
- `helix_work_items_for_build` gives you Helix-level execution details (machine, exit code, timing)
- `helix_console_for_build` gives you the raw console output from the Helix machine
- `azdo_test_summary` gives you per-job pass/fail/skip counts
- `azdo_builds_for_repo` gives you recent builds to compare against

## Flaky Test Patterns in .NET Repos

### Timing-dependent failures
- **Symptoms**: `TaskCanceledException`, "timed out", "did not complete within N seconds"
- **Root cause**: Test assumes operation completes within a fixed time, but Helix machines vary in speed
- **Fix**: Use async waits with generous timeouts, avoid `Thread.Sleep`, use `CancellationToken` with appropriate deadlines

### Race conditions
- **Symptoms**: Assertion failure with non-deterministic values, "Expected collection [A, B] but got [B, A]"
- **Root cause**: Tests depend on ordering that isn't guaranteed (e.g., concurrent operations, dictionary enumeration)
- **Fix**: Sort collections before asserting, use `Assert.Contains` instead of `Assert.Equal` for unordered collections, add synchronization

### Resource contention
- **Symptoms**: "Address already in use", "port N is occupied", "file is locked by another process"
- **Root cause**: Multiple tests (or Helix work items) sharing resources on the same machine
- **Fix**: Use dynamic port allocation, unique temp directories, proper resource cleanup

### Process/environment issues
- **Symptoms**: Exit code -1, `OutOfMemoryException`, environment variable not set
- **Root cause**: Helix machine state issues, resource exhaustion under parallel test execution
- **Fix**: Add retry logic for transient infrastructure failures, reduce parallel test count

### Network issues
- **Symptoms**: `HttpRequestException`, DNS resolution failures, connection refused
- **Root cause**: Transient network issues on Helix machines, service unavailability
- **Fix**: Add retry with exponential backoff for network calls in tests, mock external dependencies

### Test isolation failures
- **Symptoms**: Test passes in isolation but fails when run with other tests
- **Root cause**: Shared static state, MEF composition caching, global configuration
- **Fix**: Reset shared state in test setup/teardown, use unique instances per test

## Distinguishing Flaky from Real Failures

### Strong signals that a failure is flaky:
- Same test passes on retry (attempt 2+ succeeds)
- Same test passes in other recent builds on different PRs
- Same test passes on the main/CI branch
- Error pattern matches a known flaky category above
- Failure is in test infrastructure, not in the assertion itself

### Strong signals that a failure is a real regression:
- Test fails consistently across multiple attempts
- Test started failing only after the PR's changes
- Error directly references types/methods modified by the PR
- Compilation error in test or source code
- All tests in a category fail (suggests a breaking API change)

## Searching for Existing Issues

When looking for existing GitHub issues for a flaky test:
1. Search by the full test name: `is:issue "TestNamespace.TestClass.TestMethod"`
2. Search by partial name if full match fails: `is:issue "TestClass.TestMethod"`
3. Look for common labels: `flaky-test`, `test-reliability`, `Area-Infrastructure`, `bug`
4. Check both open and recently closed issues (within last 30 days)
5. If a closed issue matches, the test may have regressed — add a comment and consider reopening
