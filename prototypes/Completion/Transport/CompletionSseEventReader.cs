using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Atelia.Completion.Transport;

/// <summary>
/// One blank-line-committed server-sent event frame. This is a transport
/// representation rather than a DOM <c>MessageEvent</c>: a frame containing
/// only <c>event</c>, <c>id</c>, or <c>retry</c> is preserved.
/// </summary>
internal sealed record CompletionSseFrame(
    string? EventType,
    string? Data,
    string? Id,
    long? RetryMilliseconds
);

/// <summary>
/// Reads UTF-8 <c>text/event-stream</c> frames without assigning provider or
/// completion semantics to them.
/// </summary>
internal static class CompletionSseEventReader {
    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true
        );

    public static async IAsyncEnumerable<CompletionSseFrame> ReadFramesAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new StreamReader(
            stream,
            StrictUtf8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true
        );

        bool firstLine = true;
        string? eventType = null;
        var data = new StringBuilder();
        bool hasData = false;
        string? id = null;
        bool hasId = false;
        long? retryMilliseconds = null;

        while (await reader.ReadLineAsync(cancellationToken)
            .ConfigureAwait(false) is { } line) {
            if (firstLine) {
                firstLine = false;
                if (line.StartsWith('\uFEFF')) { line = line[1..]; }
            }

            if (line.Length == 0) {
                if (eventType is not null
                    || hasData
                    || hasId
                    || retryMilliseconds is not null) {
                    yield return new CompletionSseFrame(
                        eventType,
                        hasData ? data.ToString() : null,
                        hasId ? id : null,
                        retryMilliseconds
                    );
                }

                eventType = null;
                data.Clear();
                hasData = false;
                id = null;
                hasId = false;
                retryMilliseconds = null;
                continue;
            }

            if (line[0] == ':') { continue; }

            int colonIndex = line.IndexOf(':');
            string field;
            string value;
            if (colonIndex < 0) {
                field = line;
                value = string.Empty;
            }
            else {
                field = line[..colonIndex];
                value = line[(colonIndex + 1)..];
                if (value.StartsWith(' ')) { value = value[1..]; }
            }

            switch (field) {
                case "event":
                    eventType = value;
                    break;
                case "data":
                    if (hasData) { data.Append('\n'); }
                    data.Append(value);
                    hasData = true;
                    break;
                case "id" when !value.Contains('\0'):
                    id = value;
                    hasId = true;
                    break;
                case "retry" when IsAsciiDigits(value)
                    && long.TryParse(
                        value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out long parsedRetry
                    ):
                    retryMilliseconds = parsedRetry;
                    break;
            }
        }

        // WHATWG event streams require a blank line to commit the final
        // frame. An EOF after field lines leaves a partial frame, which is
        // deliberately discarded here.
    }

    private static bool IsAsciiDigits(string value) {
        if (value.Length == 0) { return false; }

        foreach (char character in value) {
            if (character is < '0' or > '9') { return false; }
        }
        return true;
    }
}
