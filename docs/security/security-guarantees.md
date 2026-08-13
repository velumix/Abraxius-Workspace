# Security guarantees and limits

Guaranteed by the implemented application boundary:

- runtime Lattice calls are policy checked;
- unknown/malformed requests deny;
- canonical workspace and symlink escapes deny;
- LocalOnly cloud egress denies;
- raw secret extraction denies;
- configured model and cloud-speech credentials are brokered into transports and are not retained by provider objects;
- one-shot grants are atomic and revocable;
- replayed side effects deny;
- decisions and execution results are auditable.

Not claimed: perfect host compromise resistance, universal OS sandboxing, secure remote-node transport, production platform Keychain/Credential Manager adapters, or protection from a malicious native dependency executing outside the capability boundary. Those require platform-specific defense in depth.
