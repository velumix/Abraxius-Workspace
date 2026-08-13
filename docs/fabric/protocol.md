# Fabric protocol

The baseline control transport is versioned Protocol Buffers over gRPC, HTTP/2, and TLS. Negotiation exchanges minimum, maximum, preferred versions, and feature flags. Removed protobuf fields must be reserved. Unknown or incompatible security-critical messages fail closed.

`IFabricTransport` keeps QUIC optional and capability-driven. `QuicConnection.IsSupported` is checked; Fabric correctness never depends on QUIC.
