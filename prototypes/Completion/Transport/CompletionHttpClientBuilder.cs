using System.Net.Http;
using System.Text;
using Atelia.Completion;

namespace Atelia.Completion.Transport;

/// <summary>
/// 为 Completion provider 组装可复用的 HTTP transport pipeline。
/// </summary>
public sealed class CompletionHttpClientBuilder {
    private readonly List<ICompletionHttpExchangeSink> _exchangeSinks = new();
    private HttpMessageHandler? _primaryHandler;
    private ICompletionHttpReplayResponder? _replayResponder;

    public CompletionHttpClientBuilder UsePrimaryHandler(HttpMessageHandler primaryHandler) {
        _primaryHandler = primaryHandler ?? throw new ArgumentNullException(nameof(primaryHandler));
        return this;
    }

    public CompletionHttpClientBuilder AddExchangeSink(ICompletionHttpExchangeSink sink) {
        _exchangeSinks.Add(sink ?? throw new ArgumentNullException(nameof(sink)));
        return this;
    }

    public CompletionHttpClientBuilder AddJsonLinesGoldenLogSink(string filePath) {
        return AddExchangeSink(new JsonLinesCompletionHttpExchangeFileSink(filePath));
    }

    public CompletionHttpClientBuilder UseJsonLinesReplayResponder(string filePath, string responseMediaType = "text/event-stream") {
        return UseReplayResponder(new JsonLinesCompletionHttpReplayResponder(filePath, responseMediaType));
    }

    public CompletionHttpClientBuilder UseReplayResponder(ICompletionHttpReplayResponder replayResponder) {
        _replayResponder = replayResponder ?? throw new ArgumentNullException(nameof(replayResponder));
        return this;
    }

    public HttpClient Build() {
        if (_primaryHandler is not null && _replayResponder is not null) {
            throw new InvalidOperationException("Primary handler and replay responder cannot both be configured at the same time.");
        }

        HttpMessageHandler pipeline = _replayResponder is not null
            ? new CompletionHttpReplayHandler(_replayResponder)
            : _primaryHandler ?? new HttpClientHandler();

        if (_exchangeSinks.Count > 0) {
            pipeline = new CompletionHttpCaptureHandler(_exchangeSinks) {
                InnerHandler = pipeline
            };
        }

        return new HttpClient(pipeline, disposeHandler: true);
    }

    private sealed class CompletionHttpCaptureHandler : DelegatingHandler {
        private readonly IReadOnlyList<ICompletionHttpExchangeSink> _sinks;

        public CompletionHttpCaptureHandler(IReadOnlyList<ICompletionHttpExchangeSink> sinks) {
            _sinks = sinks ?? throw new ArgumentNullException(nameof(sinks));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            ArgumentNullException.ThrowIfNull(request);

            var requestText = await ReadContentTextAsync(request.Content, cancellationToken);
            HttpResponseMessage response;
            try {
                response = await base.SendAsync(request, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) {
                PublishFailure(request, requestText, ex);
                throw;
            }

            if (response.Content is null) {
                Publish(request, response, requestText, null);
                return response;
            }

            var originalContent = response.Content;
            var originalStream = await originalContent.ReadAsStreamAsync(cancellationToken);
            var tapStream = new CompletionTapReadStream(
                originalStream,
                text => Publish(request, response, requestText, text)
            );

            response.Content = new CompletionTapStreamContent(originalContent, tapStream);
            return response;
        }

        private void Publish(HttpRequestMessage request, HttpResponseMessage response, string? requestText, string? responseText) {
            var exchange = new CompletionHttpExchange(
                Method: request.Method.Method,
                RequestUri: request.RequestUri?.ToString(),
                RequestText: requestText,
                StatusCode: (int)response.StatusCode,
                ResponseText: responseText,
                ErrorText: null
            );

            foreach (var sink in _sinks) {
                sink.OnExchange(exchange);
            }
        }

        private void PublishFailure(HttpRequestMessage request, string? requestText, Exception exception) {
            var exchange = new CompletionHttpExchange(
                Method: request.Method.Method,
                RequestUri: request.RequestUri?.ToString(),
                RequestText: requestText,
                StatusCode: null,
                ResponseText: null,
                ErrorText: CompletionHttpRequestUtility.FormatTransportFailureSummary(exception)
            );

            foreach (var sink in _sinks) {
                sink.OnExchange(exchange);
            }
        }
    }

    private sealed class CompletionHttpReplayHandler : HttpMessageHandler {
        private readonly ICompletionHttpReplayResponder _replayResponder;

        public CompletionHttpReplayHandler(ICompletionHttpReplayResponder replayResponder) {
            _replayResponder = replayResponder ?? throw new ArgumentNullException(nameof(replayResponder));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            ArgumentNullException.ThrowIfNull(request);

            var requestText = await ReadContentTextAsync(request.Content, cancellationToken);
            var response = _replayResponder.CreateResponse(
                new CompletionHttpReplayRequest(
                    Method: request.Method.Method,
                    RequestUri: request.RequestUri?.ToString(),
                    RequestText: requestText
                )
            );

            response.RequestMessage ??= request;
            return response;
        }
    }

    private static async Task<string?> ReadContentTextAsync(HttpContent? content, CancellationToken cancellationToken) {
        if (content is null) {
            return null;
        }

        await content.LoadIntoBufferAsync(cancellationToken);
        return await content.ReadAsStringAsync(cancellationToken);
    }

    private sealed class CompletionTapStreamContent : StreamContent {
        private readonly HttpContent _innerContent;

        public CompletionTapStreamContent(HttpContent innerContent, Stream stream)
            : base(stream) {
            _innerContent = innerContent ?? throw new ArgumentNullException(nameof(innerContent));

            foreach (var header in innerContent.Headers) {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override void Dispose(bool disposing) {
            base.Dispose(disposing);

            if (disposing) {
                _innerContent.Dispose();
            }
        }
    }

    private sealed class CompletionTapReadStream : Stream {
        private readonly Stream _innerStream;
        private readonly MemoryStream _buffer = new();
        private readonly Action<string?> _onCompleted;
        private bool _completed;

        public CompletionTapReadStream(Stream innerStream, Action<string?> onCompleted) {
            _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
            _onCompleted = onCompleted ?? throw new ArgumentNullException(nameof(onCompleted));
        }

        public override bool CanRead => _innerStream.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() {
            _innerStream.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count) {
            var bytesRead = _innerStream.Read(buffer, offset, count);
            CaptureChunk(buffer.AsSpan(offset, bytesRead));
            return bytesRead;
        }

        public override int Read(Span<byte> buffer) {
            var bytesRead = _innerStream.Read(buffer);
            CaptureChunk(buffer[..bytesRead]);
            return bytesRead;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            var bytesRead = await _innerStream.ReadAsync(buffer, cancellationToken);
            CaptureChunk(buffer.Span[..bytesRead]);
            return bytesRead;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) {
            var bytesRead = await _innerStream.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
            CaptureChunk(buffer.AsSpan(offset, bytesRead));
            return bytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin) {
            throw new NotSupportedException();
        }

        public override void SetLength(long value) {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) {
            throw new NotSupportedException();
        }

        public override void Write(ReadOnlySpan<byte> buffer) {
            throw new NotSupportedException();
        }

        public override ValueTask DisposeAsync() {
            Complete();
            return _innerStream.DisposeAsync();
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                Complete();
                _innerStream.Dispose();
            }

            base.Dispose(disposing);
        }

        private void CaptureChunk(ReadOnlySpan<byte> chunk) {
            if (chunk.Length > 0) {
                _buffer.Write(chunk);
                return;
            }

            Complete();
        }

        private void Complete() {
            if (_completed) {
                return;
            }

            _completed = true;
            var text = Encoding.UTF8.GetString(_buffer.ToArray());
            _onCompleted(text);
        }
    }
}
