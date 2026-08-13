# AXL binary framing

The initial codec is `AxlBinaryCodec` in `Abraxius.Axl`. It establishes a safe, versioned boundary without prematurely locking the IR to a serializer.

Frame layout, little-endian:

| Offset | Size | Meaning |
| ---: | ---: | --- |
| 0 | 4 | ASCII magic `AXLB` |
| 4 | 1 | AXL major version |
| 5 | 1 | AXL minor version |
| 6 | 1 | message type (`1` = document) |
| 7 | 1 | flags, currently zero |
| 8 | 4 | UTF-8 payload length |
| 12 | 4 | first 32 bits of SHA-256(payload) |
| 16 | 4 | reserved, currently zero |
| 20 | N | canonical AXL UTF-8 payload |

The decoder validates magic, supported version, type, length, configured maximum, checksum, strict UTF-8/AXL parsing, and semantic validation before returning a document. `AxlBinaryFramer` decodes a concatenated sequence without trusting a length beyond the configured bound.

The payload is currently canonical text, so the codec is a compact framed protocol foundation rather than a packed field-by-field encoding. This keeps compatibility and diagnostics simple. A future packed codec can implement `IAxlBinaryCodec` without changing the IR or runtime boundaries after benchmarks justify it.
