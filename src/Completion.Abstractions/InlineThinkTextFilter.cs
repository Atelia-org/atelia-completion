using System.Text;

namespace Atelia.Completion.Abstractions;

/// <summary>
/// Filters provider-leaked inline <c>&lt;think&gt;...&lt;/think&gt;</c> text from visible assistant output.
/// If the closing tag is missing, the text from the opening tag onward is dropped.
/// </summary>
public sealed class InlineThinkTextFilter {
    private const string OpenTag = "<think>";
    private const string CloseTag = "</think>";

    private string _pending = string.Empty;
    private bool _insideThink;

    public InlineThinkTextFilter(bool startInsideThink = false) {
        _insideThink = startInsideThink;
    }

    public string Filter(string delta) {
        if (string.IsNullOrEmpty(delta)) { return string.Empty; }

        _pending += delta;
        return DrainPending(flushTrailingSafeText: false);
    }

    public string FlushVisibleRemainder() {
        return DrainPending(flushTrailingSafeText: true);
    }

    public static string StripInlineThinkBlocks(string text, bool startInsideThink = false) {
        if (string.IsNullOrEmpty(text)) { return text; }

        var filter = new InlineThinkTextFilter(startInsideThink);
        string visibleText = filter.Filter(text);
        return visibleText + filter.FlushVisibleRemainder();
    }

    private string DrainPending(bool flushTrailingSafeText) {
        if (_pending.Length == 0) { return string.Empty; }

        var output = new StringBuilder();
        while (_pending.Length > 0) {
            if (_insideThink) {
                int closeIdx = _pending.IndexOf(CloseTag, StringComparison.Ordinal);
                if (closeIdx < 0) {
                    _pending = RetainTagPrefixSuffix(_pending, CloseTag, flushTrailingSafeText);
                    break;
                }

                _pending = _pending[(closeIdx + CloseTag.Length)..];
                _insideThink = false;
                continue;
            }

            int openIdx = _pending.IndexOf(OpenTag, StringComparison.Ordinal);
            if (openIdx < 0) {
                if (flushTrailingSafeText) {
                    output.Append(_pending);
                    _pending = string.Empty;
                }
                else {
                    int keepLength = FindLongestPrefixSuffix(_pending, OpenTag);
                    output.Append(_pending.AsSpan(0, _pending.Length - keepLength));
                    _pending = keepLength == 0 ? string.Empty : _pending[(_pending.Length - keepLength)..];
                }

                break;
            }

            if (openIdx > 0) {
                output.Append(_pending.AsSpan(0, openIdx));
            }

            _pending = _pending[(openIdx + OpenTag.Length)..];
            _insideThink = true;
        }

        return output.ToString();
    }

    private static string RetainTagPrefixSuffix(string text, string tag, bool flushTrailingSafeText) {
        if (flushTrailingSafeText) { return string.Empty; }

        int keepLength = FindLongestPrefixSuffix(text, tag);
        return keepLength == 0 ? string.Empty : text[(text.Length - keepLength)..];
    }

    private static int FindLongestPrefixSuffix(string text, string tag) {
        int maxLength = Math.Min(text.Length, tag.Length - 1);
        for (int length = maxLength; length > 0; length--) {
            if (text.EndsWith(tag.AsSpan(0, length), StringComparison.Ordinal)) { return length; }
        }

        return 0;
    }
}
