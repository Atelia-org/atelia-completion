namespace Atelia.Completion.Transport;

/// <summary>
/// 消费捕获到的 HTTP 文本交换。
/// </summary>
/// <remarks>
/// <see cref="CompletionHttpClientBuilder"/> 将 sink 视为 best-effort 诊断旁路：
/// 单个 sink 抛出的任何 <see cref="Exception"/> 都会被隔离，不会改变
/// provider response、transport failure 或 caller cancellation 的结果。因此 sink
/// 不得作为权威持久化或成功回执；诊断记录允许缺失。直接调用
/// <see cref="OnExchange"/> 时仍会观察到具体 sink 自身的异常。
/// </remarks>
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
