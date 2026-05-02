using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atelia.Completion.Transport;

/// <summary>
/// 将 HTTP 交换按 JSON Lines 追加写入文件，作为 MVP 阶段的 golden log 形态。
/// </summary>
/// <remarks>
/// <para>
/// 每一行都是一个 <see cref="CompletionHttpExchange"/> 的 JSON 对象，采用 camelCase 字段名。
/// </para>
/// <para>
/// 当前实现固定使用 LF 作为记录分隔符，便于后续逐行读取和顺序 replay。
/// </para>
/// </remarks>
public sealed class JsonLinesCompletionHttpExchangeFileSink : ICompletionHttpExchangeSink {
    private static readonly JsonSerializerOptions SerializerOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly string _filePath;

    public JsonLinesCompletionHttpExchangeFileSink(string filePath) {
        if (string.IsNullOrWhiteSpace(filePath)) {
            throw new ArgumentException("File path must not be blank.", nameof(filePath));
        }

        _filePath = filePath;
    }

    public void OnExchange(CompletionHttpExchange exchange) {
        ArgumentNullException.ThrowIfNull(exchange);

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory)) {
            Directory.CreateDirectory(directory);
        }

        var line = JsonSerializer.Serialize(exchange, SerializerOptions) + "\n";

        lock (_gate) {
            File.AppendAllText(_filePath, line, Utf8WithoutBom);
        }
    }
}
