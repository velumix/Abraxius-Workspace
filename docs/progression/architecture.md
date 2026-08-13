# Progression architecture

Phase 15 consumes an immutable, structured projection of the finalized Phase 14 trajectory. Agents, models, tools, and the Avalonia client cannot award progression.

```text
Trajectory
    ↓
RewardEvaluator
    ↓
ProgressionEvents
    ↓
ProgressionLedger
    ↓
Snapshot
    ↓
Avalonia / CLI
```

`ProgressionTrajectory` carries mission outcome, verification strength, meaningful graph structure, contribution facts, Skill uses, route class, eligibility, evidence references, and a project-state fingerprint. `RewardRecordId` is deterministically derived from trajectory identity plus rules version. SQLite commits the reward, events, achievements, and snapshot in one transaction. WAL mode supports responsive concurrent readers.

Snapshots are an acceleration structure. Immutable rewards remain reproducible and can rebuild the snapshot without awarding XP again.
