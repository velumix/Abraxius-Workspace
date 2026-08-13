using System.Buffers;

namespace Abraxius.Axl;

/// <summary>
/// Bounded text-message accumulator for model/network chunks. It reports incomplete syntax without
/// exposing a partially parsed command to execution. Binary streams should use AxlBinaryFramer.
/// </summary>
public sealed class AxlStreamingParser
{
    private readonly AxlLimits _limits;
    private readonly ArrayBufferWriter<byte> _buffer = new();
    private bool _completed;

    public AxlStreamingParser(AxlLimits? limits = null) => _limits = limits ?? new AxlLimits();

    public AxlParseResult Append(ReadOnlySpan<byte> chunk, bool isFinal = false)
    {
        if (_completed)
        {
            return new(
                AxlParseStatus.Invalid,
                null,
                [new(AxlDiagnosticSeverity.Error, AxlDiagnosticCode.SemanticConflict, "The AXL streaming message has already completed.")]);
        }

        if (chunk.Length > _limits.MaxDocumentBytes - _buffer.WrittenCount)
        {
            _completed = true;
            return new(
                AxlParseStatus.Invalid,
                null,
                [new(AxlDiagnosticSeverity.Error, AxlDiagnosticCode.LimitExceeded, "AXL streaming input exceeds the configured byte limit.")]);
        }

        chunk.CopyTo(_buffer.GetSpan(chunk.Length));
        _buffer.Advance(chunk.Length);
        var parsed = AxlParser.Parse(_buffer.WrittenSpan, _limits);
        if (isFinal)
        {
            _completed = true;
            return parsed;
        }

        return parsed.Status == AxlParseStatus.Invalid && parsed.Diagnostics.Length > 0 && parsed.Diagnostics.All(IsIncompleteDiagnostic)
            ? parsed with { Status = AxlParseStatus.Incomplete }
            : parsed;
    }

    public void Reset()
    {
        _buffer.Clear();
        _completed = false;
    }

    private static bool IsIncompleteDiagnostic(AxlDiagnostic diagnostic) => diagnostic.Code is
        AxlDiagnosticCode.InvalidHeader or
        AxlDiagnosticCode.UnexpectedEnd or
        AxlDiagnosticCode.UnexpectedToken or
        AxlDiagnosticCode.InvalidString;
}
