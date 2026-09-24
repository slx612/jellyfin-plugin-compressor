using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PreTranscode.Api;

public sealed record ManualOperationInfo(Guid Id, string Kind, string State, double Progress, string? Error, object? Result);

/// <summary>
/// Keeps long manual inspections alive after the HTTP request that started them has returned.
/// At most one runs at a time; completed results are held briefly for the page to collect.
/// </summary>
public sealed class ManualOperationRunner
{
    private readonly CancellationToken stopping;
    private readonly ILogger<ManualOperationRunner> logger;
    private readonly object sync = new();
    private readonly Dictionary<Guid, ManualOperationInfo> operations = new();
    private readonly Queue<Guid> order = new();
    private Guid? active;
    private CancellationTokenSource? activeCancellation;
    private Guid? latest;

    public ManualOperationRunner(CancellationToken stopping, ILogger<ManualOperationRunner> logger)
    { this.stopping = stopping; this.logger = logger; }

    public ManualOperationInfo Start(string kind, Func<IProgress<double>, CancellationToken, Task<object?>> work)
    {
        stopping.ThrowIfCancellationRequested();
        var operation = new ManualOperationInfo(Guid.NewGuid(), kind, "Running", 0, null, null);
        CancellationTokenSource cancellation;
        lock (sync)
        {
            if (active.HasValue) throw new InvalidOperationException("Ya hay una comprobación manual en curso.");
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            operations.Add(operation.Id, operation);
            order.Enqueue(operation.Id);
            while (order.Count > 10) operations.Remove(order.Dequeue());
            active = operation.Id;
            activeCancellation = cancellation;
            latest = operation.Id;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var progress = new CallbackProgress(value => Update(operation.Id, current => current with { Progress = Math.Clamp(value, 0, 100) }));
                var result = await work(progress, cancellation.Token).ConfigureAwait(false);
                Update(operation.Id, current => current with { State = "Completed", Progress = 100, Result = result });
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                Update(operation.Id, current => current with { State = "Cancelled", Error = stopping.IsCancellationRequested ? "Jellyfin se ha detenido." : "Análisis cancelado." });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Jellyfin Compressor manual {Kind} failed", kind);
                Update(operation.Id, current => current with { State = "Failed", Error = ex.Message });
            }
            finally
            {
                lock (sync)
                {
                    if (active == operation.Id) { active = null; activeCancellation = null; }
                    cancellation.Dispose();
                }
            }
        }, CancellationToken.None);
        return operation;
    }

    public ManualOperationInfo? Get(Guid id)
    {
        lock (sync) return operations.GetValueOrDefault(id);
    }

    public bool Cancel(Guid id)
    {
        CancellationTokenSource cancellation;
        lock (sync)
        {
            if (active != id || operations.GetValueOrDefault(id) is not { Kind: "Analyze", State: "Running" }
                || activeCancellation is null) return false;
            cancellation = activeCancellation;
        }
        try { cancellation.Cancel(); return true; }
        catch (ObjectDisposedException) { return false; }
    }

    public ManualOperationInfo? Latest()
    {
        lock (sync) return latest.HasValue ? operations.GetValueOrDefault(latest.Value) : null;
    }

    private void Update(Guid id, Func<ManualOperationInfo, ManualOperationInfo> change)
    {
        lock (sync)
        {
            if (operations.TryGetValue(id, out var current) && current.State == "Running") operations[id] = change(current);
        }
    }

    private sealed class CallbackProgress : IProgress<double>
    {
        private readonly Action<double> callback;
        public CallbackProgress(Action<double> callback) => this.callback = callback;
        public void Report(double value) => callback(value);
    }
}
