# Design security

Design output is untrusted external data.

- `LocalOnly` and `Secret` classifications are denied before provider traffic.
- Default generation uses synthetic content and does not upload live chat.
- Declared source scope is minimized and rooted below the workspace root.
- Provider credentials are brokered; raw values never enter DesignContext,
  artifacts, prompts, logs, or model context.
- Generated HTML is not inserted into AXAML and is never executed in a
  privileged WebView.
- Stitch cannot edit source. Implementation is an explicit existing Phase 11
  mission and remains subject to Security, Artifact review, and verification.
- Candidate identity includes source snapshot and provider metadata, so stale
  design input can be detected before implementation.

A valid provider signature or a connected Google account is not equivalent to
application authorization. Phase 17 remains the authority for egress and
credential use.
