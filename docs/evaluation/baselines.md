# Baselines

An `EvalBaseline` references an exact prior run, candidate configuration, suite version, and expected environment fingerprint. A candidate identifies the concrete commit, model, routing policy, Skill version, embedding configuration, or artifact under evaluation.

Environment snapshots include Abraxius/Git/.NET/Avalonia/AXL/security versions, OS, architecture, CPU/GPU/RAM, execution mode, actual model identities, Skill versions, routing and memory configuration, and seed where available. Unknown fields remain explicit.
