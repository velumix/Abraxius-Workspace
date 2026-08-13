# Local IPC

PluginHost uses protobuf/gRPC over Unix domain sockets on Linux/macOS and named pipes on Windows. It does not bind a public TCP port. Bootstrap authentication uses an inherited anonymous pipe, a random session ID and nonce, and expected package hash—never a command-line token. UDS permissions are restricted, payload sizes and queues are bounded, and stdout/stderr are continuously drained into a bounded log.
