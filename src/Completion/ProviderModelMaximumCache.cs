using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Atelia.Completion;

/// <summary>
/// Caches a provider-reported maximum per exact model id. Failed and canceled
/// fetches are evicted, while concurrent callers share one caller-independent
/// in-flight fetch.
/// </summary>
internal sealed class ProviderModelMaximumCache(
    Func<string, CancellationToken, Task<int>> fetch
) {
    private readonly ConcurrentDictionary<string, CacheEntry> _entries =
        new(StringComparer.Ordinal);
    private readonly Func<string, CancellationToken, Task<int>> _fetch =
        fetch ?? throw new ArgumentNullException(nameof(fetch));

    public async Task<int> GetAsync(
        string modelId,
        CancellationToken cancellationToken
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        while (true) {
            var candidate = new CacheEntry(this, modelId);
            CacheEntry entry = _entries.GetOrAdd(modelId, candidate);
            if (!ReferenceEquals(entry, candidate)) {
                candidate.DisposeUnused();
            }
            if (!entry.TryAcquire(out Task<int> operation)) {
                RemoveExact(modelId, entry);
                continue;
            }

            try {
                return await operation.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            finally {
                if (entry.ReleaseWaiter(operation)) {
                    RemoveExact(modelId, entry);
                    entry.CancelSharedFetch();
                }
            }
        }
    }

    private Task<int> StartFetch(
        string modelId,
        CacheEntry entry,
        CancellationToken sharedCancellationToken
    ) {
        Task<int> operation;
        try {
            operation = _fetch(modelId, sharedCancellationToken)
                ?? Task.FromException<int>(new InvalidOperationException(
                    "The provider model capability fetch returned null."
                ));
        }
        catch (Exception exception) {
            operation = Task.FromException<int>(exception);
        }

        _ = operation.ContinueWith(
            static (completed, state) => {
                if (completed.IsCompletedSuccessfully) { return; }
                var eviction = (EvictionState)state!;
                if (eviction.Entry.TryRetire()) {
                    eviction.Owner.RemoveExact(
                        eviction.ModelId,
                        eviction.Entry
                    );
                }
            },
            new EvictionState(this, modelId, entry),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
        return operation;
    }

    private void RemoveExact(
        string modelId,
        CacheEntry entry
    ) => _ = ((ICollection<
        KeyValuePair<string, CacheEntry>
    >)_entries).Remove(new(modelId, entry));

    private sealed class CacheEntry {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _sharedCancellation = new();
        private bool _retired;
        private int _waiterCount;

        public CacheEntry(
            ProviderModelMaximumCache owner,
            string modelId
        ) {
            Operation = new Lazy<Task<int>>(
                () => owner.StartFetch(
                    modelId,
                    this,
                    _sharedCancellation.Token
                ),
                LazyThreadSafetyMode.ExecutionAndPublication
            );
        }

        public Lazy<Task<int>> Operation { get; }

        public bool TryAcquire(out Task<int> operation) {
            lock (_gate) {
                if (_retired) {
                    operation = null!;
                    return false;
                }
                _waiterCount++;
                operation = Operation.Value;
                return true;
            }
        }

        public bool ReleaseWaiter(Task<int> operation) {
            lock (_gate) {
                if (_waiterCount <= 0) {
                    throw new InvalidOperationException(
                        "Provider model capability waiter count underflow."
                    );
                }
                _waiterCount--;
                if (_retired
                    || _waiterCount != 0
                    || operation.IsCompleted) {
                    return false;
                }
                _retired = true;
                return true;
            }
        }

        public bool TryRetire() {
            lock (_gate) {
                if (_retired) { return false; }
                _retired = true;
                return true;
            }
        }

        public void CancelSharedFetch() {
            try {
                _sharedCancellation.Cancel();
            }
            catch (ObjectDisposedException) {
                // Selected entries are not disposed while their fetch runs.
            }
        }

        public void DisposeUnused() {
            _sharedCancellation.Dispose();
        }
    }

    private sealed record EvictionState(
        ProviderModelMaximumCache Owner,
        string ModelId,
        CacheEntry Entry
    );
}

internal static class ProviderModelCapabilityResponse {
    internal const int MaximumResponseBytes = 64 * 1024;

    public static async Task<JsonDocument> ReadJsonObjectAsync(
        HttpResponseMessage response,
        string providerDisplayName,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(response);
        if (!response.IsSuccessStatusCode) {
            throw new HttpRequestException(
                $"{providerDisplayName} model capability request failed with HTTP status {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode
            );
        }
        if (response.Content.Headers.ContentLength
                is > MaximumResponseBytes) {
            throw new InvalidDataException(
                $"{providerDisplayName} model capability response exceeds the {MaximumResponseBytes}-byte bound."
            );
        }

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = GC.AllocateUninitializedArray<byte>(8192);
        while (true) {
            int read = await stream.ReadAsync(chunk, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) { break; }
            if (buffer.Length + read > MaximumResponseBytes) {
                throw new InvalidDataException(
                    $"{providerDisplayName} model capability response exceeds the {MaximumResponseBytes}-byte bound."
                );
            }
            buffer.Write(chunk, 0, read);
        }

        try {
            JsonDocument document = JsonDocument.Parse(
                buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)),
                new JsonDocumentOptions {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8
                }
            );
            if (document.RootElement.ValueKind
                    is not JsonValueKind.Object) {
                document.Dispose();
                throw new InvalidDataException(
                    $"{providerDisplayName} model capability response must be a JSON object."
                );
            }
            return document;
        }
        catch (JsonException exception) {
            throw new InvalidDataException(
                $"{providerDisplayName} model capability response is not strict bounded JSON.",
                exception
            );
        }
    }

    public static int RequirePositivePlainInt32(
        JsonElement root,
        string propertyName,
        string providerDisplayName
    ) {
        JsonElement found = default;
        var count = 0;
        foreach (JsonProperty property in root.EnumerateObject()) {
            if (!string.Equals(
                    property.Name,
                    propertyName,
                    StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            if (!string.Equals(
                    property.Name,
                    propertyName,
                    StringComparison.Ordinal)) {
                throw Malformed(providerDisplayName, propertyName);
            }
            count++;
            found = property.Value;
        }

        string raw = count == 1 ? found.GetRawText() : string.Empty;
        if (count != 1
            || found.ValueKind is not JsonValueKind.Number
            || !int.TryParse(
                raw,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int maximum
            )
            || maximum <= 0) {
            throw Malformed(providerDisplayName, propertyName);
        }
        return maximum;
    }

    private static InvalidDataException Malformed(
        string providerDisplayName,
        string propertyName
    ) => new(
        $"{providerDisplayName} model capability response requires one positive plain Int32 '{propertyName}'."
    );
}
