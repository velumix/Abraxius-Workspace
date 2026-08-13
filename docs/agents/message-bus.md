# Agent message bus

The bus is a control-plane transport, not a transcript store. It has bounded per-instance channels and bounded lossy observers for diagnostics. Control messages are typed and correlated with `MissionId`, `AssignmentId`, specialist instance, and the existing `CorrelationId`.

Model token streams and UI pulses are telemetry and must not block mission control. Specialist output should contain references and concise summaries rather than repeated source or chat history.
