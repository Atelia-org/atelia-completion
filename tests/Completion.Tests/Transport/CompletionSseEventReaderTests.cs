using System.Text;
using Atelia.Completion.Transport;
using Xunit;

namespace Atelia.Completion.Tests;

public sealed class CompletionSseEventReaderTests {
    [Fact]
    public async Task ReadFramesAsync_ParsesUtf8BomMixedLineEndingsAndKnownFields() {
        byte[] preamble = [0xEF, 0xBB, 0xBF];
        byte[] payload = Encoding.UTF8.GetBytes(
            ": comment\r\n"
            + "unknown: ignored\r\n"
            + "event: ping\r"
            + "data: first\r\n"
            + "data:second\n"
            + "id: stream-7\n"
            + "retry: 1500\r\n"
            + "\r\n"
        );
        using var stream = new MemoryStream([.. preamble, .. payload]);

        CompletionSseFrame frame = Assert.Single(
            await ReadAllAsync(stream)
        );

        Assert.Equal("ping", frame.EventType);
        Assert.Equal("first\nsecond", frame.Data);
        Assert.Equal("stream-7", frame.Id);
        Assert.Equal(1500, frame.RetryMilliseconds);
    }

    [Fact]
    public async Task ReadFramesAsync_PreservesCommittedNonDataAndEmptyDataFrames() {
        using var stream = Utf8Stream(
            "event: ping\n\n"
            + "id:\nretry: 42\n\n"
            + "data:\n\n"
        );

        IReadOnlyList<CompletionSseFrame> frames =
            await ReadAllAsync(stream);

        Assert.Collection(
            frames,
            frame => {
                Assert.Equal("ping", frame.EventType);
                Assert.Null(frame.Data);
                Assert.Null(frame.Id);
                Assert.Null(frame.RetryMilliseconds);
            },
            frame => {
                Assert.Null(frame.EventType);
                Assert.Null(frame.Data);
                Assert.Equal(string.Empty, frame.Id);
                Assert.Equal(42, frame.RetryMilliseconds);
            },
            frame => {
                Assert.Null(frame.EventType);
                Assert.Equal(string.Empty, frame.Data);
                Assert.Null(frame.Id);
                Assert.Null(frame.RetryMilliseconds);
            }
        );
    }

    [Fact]
    public async Task ReadFramesAsync_IgnoresCommentsUnknownFieldsAndInvalidValues() {
        using var stream = Utf8Stream(
            ": heartbeat\n"
            + "unknown: value\n"
            + "id: contains\0null\n"
            + "retry: +12\n"
            + "retry: 12x\n"
            + "\n"
        );

        Assert.Empty(await ReadAllAsync(stream));
    }

    [Fact]
    public async Task ReadFramesAsync_DiscardsAFrameNotCommittedBeforeEof() {
        using var stream = Utf8Stream(
            "data: complete\n\n"
            + "event: partial\n"
            + "data: discarded"
        );

        CompletionSseFrame frame = Assert.Single(
            await ReadAllAsync(stream)
        );

        Assert.Equal("complete", frame.Data);
        Assert.Null(frame.EventType);
    }

    [Fact]
    public async Task ReadFramesAsync_ReplacesMalformedUtf8() {
        byte[] bytes = [
            .. Encoding.UTF8.GetBytes("data: "),
            0xC3,
            0x28,
            .. Encoding.UTF8.GetBytes("\n\n")
        ];
        using var stream = new MemoryStream(bytes);

        CompletionSseFrame frame = Assert.Single(
            await ReadAllAsync(stream)
        );

        Assert.Equal("\uFFFD(", frame.Data);
    }

    [Fact]
    public async Task ReadFramesAsync_PreservesCallerCancellationTokenIdentity() {
        using var stream = new ControlledReadStream(
            prefix: [],
            failure: null,
            waitForCancellation: true
        );
        using var caller = new CancellationTokenSource();
        await using IAsyncEnumerator<CompletionSseFrame> enumerator =
            CompletionSseEventReader.ReadFramesAsync(
                stream,
                caller.Token
            ).GetAsyncEnumerator();

        ValueTask<bool> moveNext = enumerator.MoveNextAsync();
        caller.Cancel();
        OperationCanceledException exception =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await moveNext.AsTask()
            );

        Assert.Equal(caller.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task ReadFramesAsync_PropagatesReadFailureWithoutDispatchingPartialFrame() {
        var expected = new IOException("scripted read failure");
        using var stream = new ControlledReadStream(
            Encoding.UTF8.GetBytes("data: partial\n"),
            expected,
            waitForCancellation: false
        );
        var frames = new List<CompletionSseFrame>();

        IOException actual = await Assert.ThrowsAsync<IOException>(
            async () => {
                await foreach (CompletionSseFrame frame in
                    CompletionSseEventReader.ReadFramesAsync(stream)) {
                    frames.Add(frame);
                }
            }
        );

        Assert.Same(expected, actual);
        Assert.Empty(frames);
    }

    [Fact]
    public void RequireTerminalEvent_UsesAnUncertainTransportException() {
        CompletionStreamInterruptedException exception = Assert.Throws<
            CompletionStreamInterruptedException
        >(() => CompletionStreamTermination.RequireTerminalEvent(
            terminalEventObserved: false,
            "test-provider"
        ));

        Assert.Equal("test-provider", exception.StreamDisplayName);
        Assert.Contains(
            "outcome is uncertain",
            exception.Message,
            StringComparison.Ordinal
        );
        CompletionStreamTermination.RequireTerminalEvent(
            terminalEventObserved: true,
            "test-provider"
        );
    }

    private static MemoryStream Utf8Stream(string value)
        => new(Encoding.UTF8.GetBytes(value));

    private static async Task<IReadOnlyList<CompletionSseFrame>> ReadAllAsync(
        Stream stream,
        CancellationToken cancellationToken = default
    ) {
        var frames = new List<CompletionSseFrame>();
        await foreach (CompletionSseFrame frame in
            CompletionSseEventReader.ReadFramesAsync(
                stream,
                cancellationToken
            )) {
            frames.Add(frame);
        }
        return frames;
    }

    private sealed class ControlledReadStream(
        byte[] prefix,
        Exception? failure,
        bool waitForCancellation
    ) : Stream {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) {
            if (_offset < prefix.Length) {
                int count = Math.Min(buffer.Length, prefix.Length - _offset);
                prefix.AsSpan(_offset, count).CopyTo(buffer.Span);
                _offset += count;
                return count;
            }

            if (failure is not null) { throw failure; }
            if (!waitForCancellation) { return 0; }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException(
                "An infinite read returned without cancellation."
            );
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }
}
