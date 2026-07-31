using System.Security.Cryptography;
using System.Text.Json;
using Atelia.Completion.Abstractions;

namespace Atelia.Completion;

/// <summary>
/// Non-secret identity of one concrete Completion dispatch route. It can be
/// persisted by a consumer without copying endpoint credentials.
/// </summary>
public sealed record CompletionDispatchIdentity(
    string ConnectionId,
    string Kind,
    string ConnectionFingerprint,
    string ClientName,
    string ApiSpecId,
    string RequestAdapterFingerprint
);

/// <summary>
/// Creates stable dispatch identities from normalized connection metadata and
/// the concrete request adapter selected for that connection.
/// </summary>
public static class CompletionDispatchIdentityFactory {
    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static CompletionDispatchIdentity Create(
        CompletionConnectionConfig connection,
        ICompletionClient client
    ) {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(client);
        return new CompletionDispatchIdentity(
            connection.Id,
            connection.Kind,
            ComputeConnectionFingerprint(connection),
            client.Name,
            client.ApiSpecId,
            ComputeRequestAdapterFingerprint(client, connection)
        );
    }

    public static string ComputeConnectionFingerprint(
        CompletionConnectionConfig connection
    ) {
        ArgumentNullException.ThrowIfNull(connection);
        return ComputeFingerprint(
            new ConnectionFingerprintDto(
                connection.Id,
                connection.Kind,
                connection.ModelId,
                connection.CompletionSurfaceId,
                connection.BaseAddress,
                connection.MaxTokens
            )
        );
    }

    public static string ComputeRequestAdapterFingerprint(
        ICompletionClient client,
        CompletionConnectionConfig connection
    ) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(connection);
        return ComputeFingerprint(
            new RequestAdapterFingerprintDto(
                client.Name,
                client.ApiSpecId,
                connection.Kind,
                connection.CompletionSurfaceId
            )
        );
    }

    private static string ComputeFingerprint<T>(T value) {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            value,
            JsonOptions
        );
        return $"sha256:{Convert.ToHexStringLower(
            SHA256.HashData(bytes)
        )}";
    }

    private sealed record ConnectionFingerprintDto(
        string ConnectionId,
        string Kind,
        string ModelId,
        string CompletionSurfaceId,
        string BaseAddress,
        int? MaxTokens
    );

    private sealed record RequestAdapterFingerprintDto(
        string ClientName,
        string ClientApiSpecId,
        string ConnectionKind,
        string CompletionSurfaceId
    );
}

public enum CompletionDispatchBindingUnavailableReason {
    ConnectionMissing,
    ConnectionKindMismatch,
    ConnectionFingerprintMismatch,
    ClientNameMismatch,
    ClientApiSpecIdMismatch,
    RequestAdapterFingerprintMismatch,
}

public abstract record CompletionDispatchBindingResult {
    private CompletionDispatchBindingResult() { }

    public sealed record Bound(
        CompletionConnectionConfig Connection,
        ICompletionClient Client
    ) : CompletionDispatchBindingResult;

    public sealed record Unavailable(
        CompletionDispatchBindingUnavailableReason Reason,
        string Detail
    ) : CompletionDispatchBindingResult;
}
