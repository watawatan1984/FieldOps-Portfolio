using Microsoft.Playwright;

namespace FieldOps.E2ETests.Infrastructure;

[Collection(FieldOpsWebCollection.Name)]
public sealed class FixtureReliabilityTests(FieldOpsWebFixture fixture)
{
    [Fact]
    public async Task ClosedPageStillPreservesMarkerAndSavesTrace()
    {
        const string testName = nameof(ClosedPageStillPreservesMarkerAndSavesTrace);
        MarkerException exception = await Assert.ThrowsAsync<MarkerException>(() => fixture.RunAsync(
            testName,
            async (page, _) =>
            {
                await page.CloseAsync();
                throw new MarkerException("artifact-primary-marker");
            },
            resetDatabase: false));

        Assert.Equal("artifact-primary-marker", exception.Message);
        string runDirectory = Assert.Single(fixture.GetFailureRunDirectories(testName).TakeLast(1));
        Assert.True(File.Exists(Path.Combine(runDirectory, "trace.zip")));
        Assert.False(File.Exists(Path.Combine(runDirectory, "screenshot.png")));
        Assert.Contains("screenshot:page-closed", await File.ReadAllTextAsync(
            Path.Combine(runDirectory, "artifact-diagnostics.txt")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TraceCaptureFailureCannotReplacePrimaryMarker()
    {
        const string testName = nameof(TraceCaptureFailureCannotReplacePrimaryMarker);
        MarkerException exception = await Assert.ThrowsAsync<MarkerException>(() => fixture.RunAsync(
            testName,
            (_, _) => throw new MarkerException("trace-primary-marker"),
            resetDatabase: false,
            failureArtifactHooks: new FailureArtifactHooks(
                traceAsync: (_, _, _) => throw new InvalidOperationException("trace-secret-detail"))));

        Assert.Equal("trace-primary-marker", exception.Message);
        string runDirectory = Assert.Single(fixture.GetFailureRunDirectories(testName).TakeLast(1));
        string diagnostics = await File.ReadAllTextAsync(Path.Combine(runDirectory, "artifact-diagnostics.txt"));
        Assert.Contains("trace:InvalidOperationException", diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("trace-secret-detail", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PassingRunLeavesNoCurrentTraceOrScreenshot()
    {
        const string testName = nameof(PassingRunLeavesNoCurrentTraceOrScreenshot);
        int before = fixture.GetFailureRunDirectories(testName).Count;
        await fixture.RunAsync(testName, (_, _) => Task.CompletedTask, resetDatabase: false);
        Assert.Equal(before, fixture.GetFailureRunDirectories(testName).Count);
    }

    [Fact]
    public async Task CleanupRunnerAttemptsLaterOwnedStagesAfterFailuresAndHang()
    {
        List<string> attempted = [];
        BoundedCleanupRunner runner = new(TimeSpan.FromMilliseconds(100));
        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<string> diagnostics = await runner.RunAsync(
        [
            new("browser", _ => throw new InvalidOperationException("browser-secret")),
            new("application-process", _ => Task.Delay(Timeout.InfiniteTimeSpan)),
            new("application-log", _ => throw new IOException("log-secret")),
            new("npgsql-pool", _ => { attempted.Add("pool"); return Task.CompletedTask; }),
            new("postgres-container", _ => { attempted.Add("container"); return Task.CompletedTask; })
        ]);
        elapsed.Stop();

        Assert.Equal(["pool", "container"], attempted);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1), $"Cleanup took {elapsed.Elapsed}.");
        Assert.Contains("browser:InvalidOperationException", diagnostics);
        Assert.Contains("application-process:timeout", diagnostics);
        Assert.Contains("application-log:IOException", diagnostics);
        Assert.DoesNotContain(diagnostics, item => item.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExactExpectedForbiddenDocumentIsConsumed()
    {
        await using IBrowserContext context = await fixture.Browser.NewContextAsync(new() { BaseURL = fixture.BaseUrl });
        IPage page = await context.NewPageAsync();
        BrowserErrorCollector errors = new(page);
        await new Pages.DemoLoginPage(page).LoginAsAsync(FieldOps.Infrastructure.Identity.DemoRoleNames.SalesRepresentative);
        errors.ExpectForbiddenNavigation("/audit");
        Assert.Equal(403, (await page.GotoAsync("/audit"))!.Status);
        errors.AssertEmpty();
    }

    [Fact]
    public async Task ExpectedForbiddenDocumentDoesNotMaskUnrelatedForbiddenFetch()
    {
        await using IBrowserContext context = await fixture.Browser.NewContextAsync(new() { BaseURL = fixture.BaseUrl });
        IPage page = await context.NewPageAsync();
        BrowserErrorCollector errors = new(page);
        await new Pages.DemoLoginPage(page).LoginAsAsync(FieldOps.Infrastructure.Identity.DemoRoleNames.SalesRepresentative);
        errors.ExpectForbiddenNavigation("/audit");
        Assert.Equal(403, (await page.GotoAsync("/audit"))!.Status);
        await page.EvaluateAsync("() => fetch('/administration/reset')");
        Xunit.Sdk.XunitException exception = Assert.ThrowsAny<Xunit.Sdk.XunitException>(errors.AssertEmpty);
        Assert.Contains("unexpected-http-403:/administration/reset:fetch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingExpectedForbiddenDocumentFailsTheRun()
    {
        MarkerException exception = await Assert.ThrowsAsync<MarkerException>(() => fixture.RunAsync(
            nameof(MissingExpectedForbiddenDocumentFailsTheRun),
            (_, errors) =>
            {
                errors.ExpectForbiddenNavigation("/audit");
                throw new MarkerException("missing-forbidden-marker");
            },
            resetDatabase: false));

        Assert.Equal("missing-forbidden-marker", exception.Message);

        Xunit.Sdk.XunitException browserError = await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => fixture.RunAsync(
            $"{nameof(MissingExpectedForbiddenDocumentFailsTheRun)}Assert",
            (_, errors) =>
            {
                errors.ExpectForbiddenNavigation("/audit");
                return Task.CompletedTask;
            },
            resetDatabase: false));

        Assert.Contains("expected-http-403-not-consumed:/audit:matched=0", browserError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupRetryUsesOneDeadlineAndStopsAfterThreeAddressInUseExits()
    {
        List<int> attempts = [];
        FieldOpsStartupRetryPolicy retryPolicy = new(TimeSpan.FromMinutes(1));

        int readyAttempt = await retryPolicy.RunAsync(async (attempt, token) =>
        {
            attempts.Add(attempt);
            Assert.False(token.IsCancellationRequested);
            await Task.Yield();
            return attempt < 3
                ? FieldOpsStartupAttempt.AddressInUseExit
                : FieldOpsStartupAttempt.Ready;
        });

        Assert.Equal(3, readyAttempt);
        Assert.Equal([1, 2, 3], attempts);

        List<int> exhaustedAttempts = [];
        InvalidOperationException exhausted = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            retryPolicy.RunAsync(attempt =>
            {
                exhaustedAttempts.Add(attempt);
                return Task.FromResult(FieldOpsStartupAttempt.AddressInUseExit);
            }));

        Assert.Equal([1, 2, 3], exhaustedAttempts);
        Assert.Contains("three startup attempts", exhausted.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class MarkerException(string message) : Exception(message);
}