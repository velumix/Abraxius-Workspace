# Pairing

Discovery creates an `Unknown` endpoint only. Pairing uses a short-lived one-time random code, explicit fingerprint confirmation, and a certificate bound to the Fabric and node identity. Invitations are single use. Unpairing revokes trust and removes the local credential.

The current local credential adapter is an in-memory development adapter; production desktop adapters must bind private keys to Phase 17 platform-secure storage.
