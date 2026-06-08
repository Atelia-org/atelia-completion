using System.Text;

namespace Atelia.Completion.Abstractions;

/// <summary>
/// Provider-native reasoning/replay block 的稳定、后端无关序列化表示。
/// </summary>
public sealed record SerializedReasoningBlock(
    string CodecId,
    string OriginProviderId,
    string OriginApiSpecId,
    string OriginModel,
    byte[] Payload,
    string? PlainTextForDebug
) {
    public CompletionDescriptor ToOrigin()
        => new(OriginProviderId, OriginApiSpecId, OriginModel);

    public static SerializedReasoningBlock Create(
        string codecId,
        CompletionDescriptor origin,
        byte[] payload,
        string? plainTextForDebug
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(codecId);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(payload);

        return new SerializedReasoningBlock(
            codecId,
            origin.ProviderId,
            origin.ApiSpecId,
            origin.Model,
            payload.ToArray(),
            plainTextForDebug
        );
    }

    internal ActionBlock.TextReasoningBlock ToFallbackTextReasoningBlock()
        => new(PlainTextForDebug ?? string.Empty, ToOrigin(), PlainTextForDebug);
}

/// <summary>
/// 将一个具体 <see cref="ActionBlock.ReasoningBlock"/> 子类型转换为稳定 replay payload 的 codec。
/// </summary>
public interface IReasoningBlockCodec {
    string CodecId { get; }

    bool CanEncode(ActionBlock.ReasoningBlock block);

    SerializedReasoningBlock Encode(ActionBlock.ReasoningBlock block);

    ActionBlock.ReasoningBlock Decode(SerializedReasoningBlock serialized);
}

/// <summary>
/// 按稳定 codec id 维护 provider-native reasoning block 的可逆转换表。
/// </summary>
public sealed class ReasoningBlockCodecRegistry {
    private readonly object _gate = new();
    private readonly Dictionary<string, IReasoningBlockCodec> _byId = new(StringComparer.Ordinal);
    private readonly List<IReasoningBlockCodec> _codecs = new();

    public static ReasoningBlockCodecRegistry Shared { get; } = CreateDefault();

    public static ReasoningBlockCodecRegistry CreateDefault() {
        var registry = new ReasoningBlockCodecRegistry();
        registry.Register(new TextReasoningBlockCodec());
        return registry;
    }

    public IReadOnlyList<IReasoningBlockCodec> Codecs {
        get {
            lock (_gate) {
                return _codecs.ToArray();
            }
        }
    }

    public void Register(IReasoningBlockCodec codec) {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentException.ThrowIfNullOrWhiteSpace(codec.CodecId);

        lock (_gate) {
            if (_byId.TryGetValue(codec.CodecId, out var existing)) {
                if (existing.GetType() == codec.GetType()) { return; }

                throw new InvalidOperationException(
                    $"Reasoning codec id '{codec.CodecId}' is already registered by '{existing.GetType().FullName}'."
                );
            }

            _byId.Add(codec.CodecId, codec);
            _codecs.Add(codec);
        }
    }

    public SerializedReasoningBlock Encode(ActionBlock.ReasoningBlock block) {
        ArgumentNullException.ThrowIfNull(block);

        IReasoningBlockCodec[] snapshot;
        lock (_gate) {
            snapshot = _codecs.ToArray();
        }

        foreach (var codec in snapshot) {
            if (codec.CanEncode(block)) { return codec.Encode(block); }
        }

        throw new InvalidOperationException(
            $"No reasoning block codec is registered for '{block.GetType().FullName}'. " +
            "Register a provider codec before persisting provider-native reasoning."
        );
    }

    public ActionBlock.ReasoningBlock Decode(SerializedReasoningBlock serialized) {
        ArgumentNullException.ThrowIfNull(serialized);

        IReasoningBlockCodec? codec;
        lock (_gate) {
            _byId.TryGetValue(serialized.CodecId, out codec);
        }

        return codec is null
            ? serialized.ToFallbackTextReasoningBlock()
            : codec.Decode(serialized);
    }

    private sealed class TextReasoningBlockCodec : IReasoningBlockCodec {
        public const string Id = "atelia.reasoning.text.v1";

        public string CodecId => Id;

        public bool CanEncode(ActionBlock.ReasoningBlock block)
            => block is ActionBlock.TextReasoningBlock;

        public SerializedReasoningBlock Encode(ActionBlock.ReasoningBlock block) {
            if (block is not ActionBlock.TextReasoningBlock textBlock) { throw new ArgumentException("Codec can only encode TextReasoningBlock.", nameof(block)); }

            return SerializedReasoningBlock.Create(
                CodecId,
                textBlock.Origin,
                Encoding.UTF8.GetBytes(textBlock.Content),
                textBlock.PlainTextForDebug
            );
        }

        public ActionBlock.ReasoningBlock Decode(SerializedReasoningBlock serialized) {
            ArgumentNullException.ThrowIfNull(serialized);
            var content = Encoding.UTF8.GetString(serialized.Payload);
            return new ActionBlock.TextReasoningBlock(
                content,
                serialized.ToOrigin(),
                serialized.PlainTextForDebug ?? content
            );
        }
    }
}
