# Resource governor

The governor atomically grants or rejects host RAM and all requested device-memory reservations. It preserves configurable RAM/device headroom and never leaves partial multi-GPU reservations. Reservations are typed, expiring, inspectable, and released after unload or failure.

Inference priorities range from realtime voice to maintenance. Queues are bounded and use burst-limited priority so interactive work wins without permanently starving background work.
