using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atelia.Completion.Abstractions;

/// <summary>
/// <see cref="ActionMessage"/> 的稳定序列化辅助层。
/// JSON / StateJournal / golden fixture 等后端应共享这里定义的 block surrogate，而不是各自解释 provider-native reasoning。
/// </summary>
public static class ActionMessageSerialization {
    public const string BlockKindText = "text";
    public const string BlockKindToolCall = "tool-call";
    public const string BlockKindTextToolCall = "text-tool-call";
    public const string BlockKindReasoning = "reasoning";

    private static readonly JsonSerializerOptions DefaultJsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize(ActionMessage message, ReasoningBlockCodecRegistry? registry = null, JsonSerializerOptions? options = null) {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.Serialize(ToSerializedBlocks(message.Blocks, registry), options ?? DefaultJsonOptions);
    }

    public static ActionMessage Deserialize(string json, ReasoningBlockCodecRegistry? registry = null, JsonSerializerOptions? options = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return new ActionMessage(DeserializeBlocks(json, registry, options));
    }

    public static string SerializeBlocks(IReadOnlyList<ActionBlock> blocks, ReasoningBlockCodecRegistry? registry = null, JsonSerializerOptions? options = null) {
        ArgumentNullException.ThrowIfNull(blocks);
        return JsonSerializer.Serialize(ToSerializedBlocks(blocks, registry), options ?? DefaultJsonOptions);
    }

    public static ActionBlock[] DeserializeBlocks(string json, ReasoningBlockCodecRegistry? registry = null, JsonSerializerOptions? options = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var dtos = JsonSerializer.Deserialize<SerializedActionBlock[]>(json, options ?? DefaultJsonOptions)
                   ?? Array.Empty<SerializedActionBlock>();
        return FromSerializedBlocks(dtos, registry);
    }

    public static SerializedActionBlock[] ToSerializedBlocks(IReadOnlyList<ActionBlock> blocks, ReasoningBlockCodecRegistry? registry = null) {
        ArgumentNullException.ThrowIfNull(blocks);
        registry ??= ReasoningBlockCodecRegistry.Shared;

        var result = new SerializedActionBlock[blocks.Count];
        for (int i = 0; i < blocks.Count; i++) {
            result[i] = blocks[i] switch {
                ActionBlock.Text text => new SerializedActionBlock(BlockKindText, text.Content, null, null, null, null),
                ActionBlock.ToolCall toolCall => new SerializedActionBlock(
                    toolCall.Call.InputKind == ToolInputKind.Text ? BlockKindTextToolCall : BlockKindToolCall,
                    null,
                    toolCall.Call.ToolName,
                    toolCall.Call.ToolCallId,
                    toolCall.Call.InputKind == ToolInputKind.JsonObject ? toolCall.Call.RawInput : null,
                    null
                ) {
                    InputKind = toolCall.Call.InputKind == ToolInputKind.Text ? ToolInputKind.Text : null,
                    RawInput = toolCall.Call.InputKind == ToolInputKind.Text ? toolCall.Call.RawInput : null
                },
                ActionBlock.ReasoningBlock reasoning => new SerializedActionBlock(
                    BlockKindReasoning,
                    null,
                    null,
                    null,
                    null,
                    registry.Encode(reasoning)
                ),
                _ => throw new InvalidOperationException($"Unsupported action block type '{blocks[i].GetType().FullName}'.")
            };
        }

        return result;
    }

    public static ActionBlock[] FromSerializedBlocks(IReadOnlyList<SerializedActionBlock> blocks, ReasoningBlockCodecRegistry? registry = null) {
        ArgumentNullException.ThrowIfNull(blocks);
        registry ??= ReasoningBlockCodecRegistry.Shared;

        var result = new ActionBlock[blocks.Count];
        for (int i = 0; i < blocks.Count; i++) {
            var dto = blocks[i] ?? throw new InvalidDataException($"Serialized action block at index {i} is null.");
            result[i] = dto.Kind switch {
                BlockKindText when dto.Content is not null => new ActionBlock.Text(dto.Content),
                BlockKindToolCall or BlockKindTextToolCall when dto.ToolName is not null && dto.ToolCallId is not null => new ActionBlock.ToolCall(
                    DecodeToolCall(dto)
                ),
                BlockKindReasoning when dto.Reasoning is not null => registry.Decode(dto.Reasoning),
                _ => throw new InvalidDataException($"Unsupported serialized action block kind '{dto.Kind}'.")
            };
        }

        return result;
    }

    private static RawToolCall DecodeToolCall(SerializedActionBlock dto) {
        return dto.InputKind switch {
            null or ToolInputKind.JsonObject when dto.Kind == BlockKindToolCall && dto.RawInput is null =>
                new RawToolCall(dto.ToolName!, dto.ToolCallId!, dto.RawArgumentsJson ?? "{}"),
            ToolInputKind.Text when dto.Kind == BlockKindTextToolCall && dto.RawInput is not null && dto.RawArgumentsJson is null =>
                RawToolCall.FromText(dto.ToolName!, dto.ToolCallId!, dto.RawInput),
            _ => throw new InvalidDataException("Invalid tool input kind or conflicting input payloads.")
        };
    }
}

public sealed record SerializedActionBlock(
    string Kind,
    string? Content,
    string? ToolName,
    string? ToolCallId,
    string? RawArgumentsJson,
    SerializedReasoningBlock? Reasoning
) {
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolInputKind? InputKind { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RawInput { get; init; }
}
