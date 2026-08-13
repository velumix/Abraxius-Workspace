# Memory estimation

Memory is estimated as weights + KV/context cache + scratch graph + backend overhead + host RAM + safety headroom. KV cost scales with context, layers, hidden width, element width, and concurrency. Low-confidence first loads receive greater headroom.

Observed RAM/device usage is fed back per exact variant/backend/device set. This calibration improves estimates without changing historical execution identity.
