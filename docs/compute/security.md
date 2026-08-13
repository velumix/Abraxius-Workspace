# Compute security

Model repositories are untrusted data. Abraxius does not enable `trust_remote_code`, execute repository scripts, or infer authorization from model metadata. Imports use bounded format inspection and native loading belongs in restricted/supervised processes where practical.

Managed model servers bind to loopback. Downloads, process launch, model paths, secret-backed registries, and Fabric sharing remain Phase 17 operations. Prompt bodies and secrets are excluded from ordinary telemetry.
