using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace WachuMakeyMaking.Utils;

public sealed class BatchProcessor<TId, TResult> : IDisposable
    where TId : notnull
{
    private readonly Channel<TId> channel = Channel.CreateUnbounded<TId>(
        new UnboundedChannelOptions { SingleReader = true }
    );

    private readonly ConcurrentDictionary<TId, TaskCompletionSource<TResult>> inFlight = new();
    private readonly ConcurrentDictionary<TId, (TResult Value, DateTime Expiration)> cache = new();

    private readonly Func<List<TId>, CancellationToken, Task<Dictionary<TId, TResult>>> batchFetcher;
    private readonly TimeSpan cacheTtl;
    private readonly int batchSize;
    private readonly TimeSpan batchTimeout;

    private readonly CancellationTokenSource tokenSource = new();
    private readonly Task workerTask;

    public BatchProcessor(
        Func<List<TId>, CancellationToken, Task<Dictionary<TId, TResult>>> batchFetcher,
        int batchSize = 100,
        TimeSpan? batchTimeout = null,
        TimeSpan? cacheTtl = null
    )
    {
        this.batchFetcher = batchFetcher;
        this.batchSize = batchSize;
        this.batchTimeout = batchTimeout ?? TimeSpan.FromMilliseconds(50);
        this.cacheTtl = cacheTtl ?? TimeSpan.FromMinutes(10);

        // Start background batch reader on plugin startup
        workerTask = Task.Run(() => WorkerLoopAsync(tokenSource.Token));
    }

    /// <summary>
    /// Gets item from cache or enqueues it for batch processing.
    /// </summary>
    public async Task<TResult> GetOrFetchAsync(TId id, CancellationToken ct = default)
    {
        // 1. Return cached value if valid
        if (cache.TryGetValue(id, out var entry) && entry.Expiration > DateTime.UtcNow)
        {
            return entry.Value;
        }

        // 2. Deduplicate: Attach to existing in-flight task or create a new one
        var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actualTcs = inFlight.GetOrAdd(id, tcs);

        if (ReferenceEquals(tcs, actualTcs))
        {
            await channel.Writer.WriteAsync(id, ct);
        }

        return await actualTcs.Task.WaitAsync(ct);
    }

    /// <summary>
    /// Gets multiple items from cache or enqueues missing IDs for batch processing.
    /// </summary>
    public async Task<IReadOnlyDictionary<TId, TResult>> GetOrFetchAsync(
        IEnumerable<TId> ids,
        CancellationToken ct = default
    )
    {
        var distinctIds = ids.Distinct().ToList();
        var results = new Dictionary<TId, TResult>(distinctIds.Count);
        var pendingTasks = new List<(TId Id, Task<TResult> Task)>();

        // 1. Partition into cached items and missing items
        foreach (var id in distinctIds)
        {
            if (cache.TryGetValue(id, out var entry) && entry.Expiration > DateTime.UtcNow)
            {
                results[id] = entry.Value;
            }
            else
            {
                // Delegates to single GetOrFetchAsync: handles deduplication & in-flight tracking
                pendingTasks.Add((id, GetOrFetchAsync(id, ct)));
            }
        }

        // Fast path: All items were already cached
        if (pendingTasks.Count == 0)
        {
            return results;
        }

        // 2. Await all missing batch requests concurrently
        await Task.WhenAll(pendingTasks.Select(p => p.Task));

        // 3. Collect completed results
        foreach (var (id, task) in pendingTasks)
        {
            results[id] = await task; // Resolves immediately as Task.WhenAll completed
        }

        return results;
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        var reader = channel.Reader;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await reader.WaitToReadAsync(ct))
                    break;

                var batch = new List<TId>();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(batchTimeout);

                try
                {
                    while (batch.Count < batchSize && reader.TryRead(out var id))
                    {
                        batch.Add(id);
                    }

                    while (batch.Count < batchSize && !timeoutCts.IsCancellationRequested)
                    {
                        if (await reader.WaitToReadAsync(timeoutCts.Token) && reader.TryRead(out var nextId))
                        {
                            batch.Add(nextId);
                        }
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Batch timeout reached - process whatever was collected
                }

                if (batch.Count > 0)
                {
                    await ProcessBatchAsync(batch, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break; // Plugin is unloading
            }
        }
    }

    private async Task ProcessBatchAsync(List<TId> batch, CancellationToken ct)
    {
        try
        {
            var results = await batchFetcher(batch, ct);
            var expiration = DateTime.UtcNow.Add(cacheTtl);

            foreach (var id in batch)
            {
                if (results.TryGetValue(id, out var result))
                {
                    cache[id] = (result, expiration);

                    if (inFlight.TryRemove(id, out var tcs))
                    {
                        tcs.TrySetResult(result);
                    }
                }
                else if (inFlight.TryRemove(id, out var tcs))
                {
                    tcs.TrySetException(new KeyNotFoundException($"ID {id} was not returned by fetcher."));
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var id in batch)
            {
                if (inFlight.TryRemove(id, out var tcs))
                {
                    tcs.TrySetException(ex);
                }
            }
        }
    }

    public void Dispose()
    {
        channel.Writer.TryComplete();
        tokenSource.Cancel();
        tokenSource.Dispose();
    }
}
