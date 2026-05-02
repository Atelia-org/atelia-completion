using Atelia.Diagnostics;

namespace Atelia.Completion.Transport;

/// <summary>
/// 消费捕获到的 HTTP 文本交换。
/// </summary>
public interface ICompletionHttpExchangeSink {
    void OnExchange(CompletionHttpExchange exchange);
}

/// <summary>
/// 将捕获到的 HTTP 交换保存在内存中，便于单测断言或后续自定义落盘。
/// </summary>
public sealed class InMemoryCompletionHttpExchangeSink : ICompletionHttpExchangeSink {
    private readonly object _gate = new();
    private readonly List<CompletionHttpExchange> _exchanges = new();

    public void OnExchange(CompletionHttpExchange exchange) {
        ArgumentNullException.ThrowIfNull(exchange);

        lock (_gate) {
            _exchanges.Add(exchange);
        }
    }

    public IReadOnlyList<CompletionHttpExchange> GetSnapshot() {
        lock (_gate) {
            return _exchanges.ToArray();
        }
    }
}

/// <summary>
/// 使用 <see cref="DebugUtil"/> 输出 HTTP 交换文本，便于按 category 做统一开关。
/// </summary>
public sealed class DebugCompletionHttpExchangeSink : ICompletionHttpExchangeSink {
    private readonly string _category;
    private readonly int _maxTextLength;

    public DebugCompletionHttpExchangeSink(string category = "Provider.Http", int maxTextLength = 4096) {
        if (string.IsNullOrWhiteSpace(category)) {
            throw new ArgumentException("Category must not be blank.", nameof(category));
        }

        if (maxTextLength <= 0) {
            throw new ArgumentOutOfRangeException(nameof(maxTextLength), maxTextLength, "Max text length must be positive.");
        }

        _category = category;
        _maxTextLength = maxTextLength;
    }

    public void OnExchange(CompletionHttpExchange exchange) {
        ArgumentNullException.ThrowIfNull(exchange);

        var requestText = Shorten(exchange.RequestText);
        var responseText = Shorten(exchange.ResponseText);
        var errorText = Shorten(exchange.ErrorText);
        DebugUtil.Trace(
            _category,
            $"HTTP {exchange.Method} {exchange.RequestUri ?? "<null>"} status={exchange.StatusCode?.ToString() ?? "<null>"}\n"
            + $"request:\n{requestText}\n"
            + $"response:\n{responseText}\n"
            + $"error:\n{errorText}"
        );
    }

    private string Shorten(string? text) {
        if (text is null) {
            return "<null>";
        }

        if (text.Length <= _maxTextLength) {
            return text;
        }

        return text[.._maxTextLength] + "\n...<truncated>";
    }
}
