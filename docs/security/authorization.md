# Authorization

`AuthorizationRequest` carries a typed subject, immutable proposed action, canonical resource, mission context, classification, budgets, and minimum sandbox. `AuthorizationDecision` is `Allow`, `Deny`, `RequireApproval`, or `AllowWithConstraints`, with a stable reason and explicit policy trace.

Harmless reads inside an approved workspace are batched by policy. External effects, credential use, destructive actions, scope expansion, and unavailable required sandboxes cross an authority boundary. Vague intent never becomes a grant; changed security-sensitive parameters require a new request.

One-shot grants are atomically consumed. Mission grants match the subject, mission, capability, and exact resource scope and expire when the mission ends or when revoked.
