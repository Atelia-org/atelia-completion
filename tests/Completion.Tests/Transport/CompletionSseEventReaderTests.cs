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
    public async Task ReadFramesAsync_RejectsMalformedUtf8() {
        byte[] bytes = [
            .. Encoding.UTF8.GetBytes("data: "),
            0xC3,
            0x28,
            .. Encoding.UTF8.GetBytes("\n\n")
        ];
        using var stream = new MemoryStream(bytes);

        await Assert.ThrowsAsync<DecoderFallbackException>(
            async () => await ReadAllAsync(stream)
        );
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
}
