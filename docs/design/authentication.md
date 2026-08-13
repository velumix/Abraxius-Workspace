# Google authentication

Interactive connection uses installed-application OAuth:

1. Bind a one-shot loopback listener on `127.0.0.1` and an ephemeral port.
2. Generate a cryptographically random OAuth `state` and PKCE verifier.
3. Open the system browser with the authorization URL.
4. Validate the callback state before exchanging the authorization code.
5. Store access and refresh credentials only through `ISecretStore`.
6. Issue a session-scoped Phase 17 grant for the explicit Stitch connection.

No token is placed on a process command line, in AXL, in the transcript, or
in generic telemetry. The callback returns a minimal completion page and is
closed after one request.

The current installation uses the configured Phase 17 store. In the default
development host that store is process-local with environment-backed fallback;
an OS keychain-backed store should be selected by the deployment profile before
claiming restart-persistent OAuth credentials.
