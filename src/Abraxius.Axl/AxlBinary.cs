using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Abraxius.Axl;

public interface IAxlBinaryCodec
{
    byte[] Encode(AxlDocument document);
    bool TryDecode(ReadOnlySpan<byte> frame, out AxlDocument? document, out AxlDiagnostic? diagnostic);
}

public sealed record AxlBinaryCodecOptions(
    int MaximumPayloadBytes = 4 * 1024 * 1024,
    bool ValidateSemanticDocument = true);

public sealed class AxlBinaryCodec : IAxlBinaryCodec
{
    public const int HeaderSize = 20;
    public const byte DocumentType = 1;
    private static ReadOnlySpan<byte> Magic => "AXLB"u8;
    private readonly AxlBinaryCodecOptions _options;

    public AxlBinaryCodec(AxlBinaryCodecOptions? options = null) => _options = options ?? new AxlBinaryCodecOptions();

    public byte[] Encode(AxlDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (_options.ValidateSemanticDocument && AxlValidator.Validate(document).Any(static diagnostic => diagnostic.Severity == AxlDiagnosticSeverity.Error))
        {
            throw new InvalidOperationException("Cannot encode an AXL document that fails semantic validation.");
        }

        var text = AxlFormatter.Compact(document);
        var payload = Encoding.UTF8.GetBytes(text);
        if (payload.Length > _options.MaximumPayloadBytes)
        {
            throw new InvalidOperationException("AXL binary payload exceeds the configured maximum.");
        }

        var result = new byte[HeaderSize + payload.Length];
        Magic.CopyTo(result);
        result[4] = checked((byte)document.Version.Major);
        result[5] = checked((byte)document.Version.Minor);
        result[6] = DocumentType;
        result[7] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8, 4), checked((uint)payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, 4), Checksum(payload));
        payload.CopyTo(result.AsSpan(HeaderSize));
        return result;
    }

    public bool TryDecode(ReadOnlySpan<byte> frame, out AxlDocument? document, out AxlDiagnostic? diagnostic)
    {
        document = null;
        diagnostic = null;
        if (frame.Length < HeaderSize || !frame[..4].SequenceEqual(Magic))
        {
            diagnostic = new(AxlDiagnosticSeverity.Error, AxlDiagnosticCode.BinaryInvalid, "AXL binary frame has an invalid magic header.");
            return false;
        }

        var version = new AxlVersion(frame[4], frame[5]);
        if (version.Major != AxlVersion.Current.Major || version.Minor > AxlVersion.Current.Minor)
        {
            diagnostic = new(AxlDiagnosticSeverity.Error, AxlDiagnosticCode.BinaryUnsupportedVersion, $"AXL binary version {version} is unsupported.");
            return false;
        }

        if (frame[6] != DocumentType)
        {
            diagnostic = new(AxlDiagnosticSeverity.Error, AxlDiagnosticCode.BinaryInvalid, $"Unknown AXL binary message type {frame[6]}.");
            return false;
        }

        if (frame[7] != 0 || frame[16] != 0 || frame[17] != 0 || frame[18] != 0 || frame[19] != 0)
        {
            diagnostic = new(AxlDiagnosticSeverity.Error, AxlDiagnosticCode.BinaryInvalid, "AXL binary frame contains unsupported flags or reserved data.");
            return false;
        }

        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(frame[8..12]);
        if (payloadLength > _options.MaximumPayloadBytes || payloadLength != frame.Length - HeaderSize)
        {
            diagnostic = new(AxlDiagnosticSeverity.Error, AxlDiagnosticCode.BinaryTooLarge, "AXL binary payload length is invalid or exceeds the configured maximum.");
            return false;
        }

        var payload = frame[HeaderSize..];
        if (BinaryPrimitives.ReadUInt32LittleEndian(frame[12..16]) != Checksum(payload))
        {
            diagnostic = new(AxlDiagnosticSeverity.Error, AxlDiagnosticCode.BinaryInvalid, "AXL binary payload checksum does not match.");
            return false;
        }

        var parsed = AxlParser.Parse(payload, new AxlLimits(MaxDocumentBytes: _options.MaximumPayloadBytes));
        if (!parsed.IsSuccess || parsed.Document is null)
        {
            diagnostic = parsed.Diagnostics.FirstOrDefault() ?? new(AxlDiagnosticSeverity.Error, AxlDiagnosticCode.BinaryInvalid, "AXL binary payload is not valid AXL text.");
            return false;
        }

        if (_options.ValidateSemanticDocument)
        {
            var validation = AxlValidator.Validate(parsed.Document);
            if (validation.Length > 0)
            {
                diagnostic = validation[0];
                return false;
            }
        }

        document = parsed.Document;
        return true;
    }

    private static uint Checksum(ReadOnlySpan<byte> payload)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(payload, digest);
        return BinaryPrimitives.ReadUInt32LittleEndian(digest);
    }
}

public static class AxlBinaryFramer
{
    public static ImmutableArray<AxlDocument> DecodeMany(ReadOnlySpan<byte> bytes, AxlBinaryCodec? codec = null)
    {
        codec ??= new AxlBinaryCodec();
        var documents = ImmutableArray.CreateBuilder<AxlDocument>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < AxlBinaryCodec.HeaderSize)
            {
                throw new InvalidDataException("Truncated AXL binary frame header.");
            }

            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 8)..(offset + 12)]);
            var frameLength = (ulong)AxlBinaryCodec.HeaderSize + length;
            if (frameLength > int.MaxValue || frameLength > (ulong)(bytes.Length - offset))
            {
                throw new InvalidDataException("Truncated AXL binary frame payload.");
            }

            var boundedFrameLength = (int)frameLength;
            if (!codec.TryDecode(bytes.Slice(offset, boundedFrameLength), out var document, out var diagnostic) || document is null)
            {
                throw new InvalidDataException(diagnostic?.Message ?? "Invalid AXL binary frame.");
            }

            documents.Add(document);
            offset += boundedFrameLength;
        }

        return documents.ToImmutable();
    }
}

public interface IAxlMigration
{
    AxlVersion From { get; }
    AxlVersion ToVersion { get; }
    AxlDocument Migrate(AxlDocument document);
}

public sealed class AxlMigrationRegistry
{
    private readonly ImmutableArray<IAxlMigration> _migrations;

    public AxlMigrationRegistry(IEnumerable<IAxlMigration>? migrations = null) => _migrations = migrations?.ToImmutableArray() ?? ImmutableArray<IAxlMigration>.Empty;

    public AxlDocument MigrateToCurrent(AxlDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var current = document;
        var visited = new HashSet<AxlVersion>();
        while (current.Version != AxlVersion.Current)
        {
            if (!visited.Add(current.Version))
            {
                throw new InvalidOperationException($"AXL migration cycle detected at {current.Version}.");
            }

            var migration = _migrations
                .Where(candidate => candidate.From == current.Version)
                .OrderBy(candidate => candidate.ToVersion.Major)
                .ThenBy(candidate => candidate.ToVersion.Minor)
                .FirstOrDefault();
            if (migration is null)
            {
                break;
            }

            var migrated = migration.Migrate(current);
            if (migrated.Version != migration.ToVersion)
            {
                throw new InvalidOperationException($"AXL migration from {migration.From} declared {migration.ToVersion} but returned {migrated.Version}.");
            }

            current = migrated;
        }

        if (current.Version != AxlVersion.Current)
        {
            throw new InvalidOperationException($"No compatible migration exists for {current.Version}.");
        }

        return current;
    }
}

public static class AxlVersionNegotiator
{
    public static bool CanRead(AxlVersion version) => version.Major == AxlVersion.Current.Major && version.Minor <= AxlVersion.Current.Minor;

    public static AxlVersion Negotiate(IEnumerable<AxlVersion> peerVersions)
    {
        var compatible = peerVersions.Where(CanRead).OrderByDescending(static version => version.Minor).FirstOrDefault();
        return compatible == default ? throw new InvalidOperationException("No compatible AXL version was offered.") : compatible;
    }
}
