using Jellyfin.Plugin.PreTranscode.Api;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.PreTranscode.Tests;

public class ManualOperationRunnerTests
{
    [Fact]
    public async Task StartReturnsBeforeSlowWorkAndKeepsItsResult()
    {
        var runner = new ManualOperationRunner(CancellationToken.None, NullLogger<ManualOperationRunner>.Instance);
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = runner.Start("Analyze", (_, _) => release.Task);

        Assert.Equal("Running", runner.Get(started.Id)!.State);
        release.SetResult(new[] { "film" });
        var finished = await WaitForEnd(runner, started.Id);
        Assert.Equal("Completed", finished.State);
        Assert.Equal(new[] { "film" }, finished.Result);
    }

    [Fact]
    public async Task ConcurrentManualRequestsAreRejectedAndFailureIsVisible()
    {
        var runner = new ManualOperationRunner(CancellationToken.None, NullLogger<ManualOperationRunner>.Instance);
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = runner.Start("Movie", (_, _) => release.Task);
        Assert.Throws<InvalidOperationException>(() => runner.Start("Analyze", (_, _) => Task.FromResult<object?>(null)));

        release.SetException(new IOException("No se puede leer la película."));
        var finished = await WaitForEnd(runner, started.Id);
        Assert.Equal("Failed", finished.State);
        Assert.Contains("No se puede leer", finished.Error);
        Assert.Equal("Running", runner.Start("Analyze", (_, _) => Task.FromResult<object?>(null)).State);
    }

    [Fact]
    public async Task ProgressIsReportedWhileTheRequestIsRunning()
    {
        var runner = new ManualOperationRunner(CancellationToken.None, NullLogger<ManualOperationRunner>.Instance);
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = runner.Start("Analyze", (progress, _) => { progress.Report(42); return release.Task; });
        await WaitForProgress(runner, started.Id, 42);
        Assert.Equal(42, runner.Get(started.Id)!.Progress);
        release.SetResult(null);
        await WaitForEnd(runner, started.Id);
    }

    [Fact]
    public async Task LatestResultCanBeRecoveredAfterWorkFinishes()
    {
        var runner = new ManualOperationRunner(CancellationToken.None, NullLogger<ManualOperationRunner>.Instance);
        var started = runner.Start("Analyze", (_, _) => Task.FromResult<object?>(new[] { "film" }));
        await WaitForEnd(runner, started.Id);
        Assert.Equal(started.Id, runner.Latest()!.Id);
        Assert.Equal("Completed", runner.Latest()!.State);
        Assert.True(runner.Cancel(started.Id));
    }

    [Fact]
    public async Task RunningAnalysisCanBeCancelledWithoutStoppingJellyfin()
    {
        var runner = new ManualOperationRunner(CancellationToken.None, NullLogger<ManualOperationRunner>.Instance);
        var started = runner.Start("Analyze", async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        });

        Assert.True(runner.Cancel(started.Id));
        var finished = await WaitForEnd(runner, started.Id);
        Assert.Equal("Cancelled", finished.State);
        Assert.True(runner.Cancel(started.Id));
        Assert.Equal("Running", runner.Start("Analyze", (_, _) => Task.FromResult<object?>(null)).State);
    }

    [Fact]
    public void CancelDoesNotStopACompressionOperation()
    {
        var runner = new ManualOperationRunner(CancellationToken.None, NullLogger<ManualOperationRunner>.Instance);
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = runner.Start("Compress", (_, _) => release.Task);
        Assert.False(runner.Cancel(started.Id));
        release.SetResult(null);
    }

    private static async Task<ManualOperationInfo> WaitForEnd(ManualOperationRunner runner, Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (runner.Get(id) is { State: "Running" }) await Task.Delay(10, timeout.Token);
        return runner.Get(id)!;
    }

    private static async Task WaitForProgress(ManualOperationRunner runner, Guid id, double value)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (runner.Get(id)!.Progress < value) await Task.Delay(10, timeout.Token);
    }
}
