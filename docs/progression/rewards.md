# Rewards

`ProgressionRulesV1` owns all pacing constants. The evaluator computes:

```text
base XP
× difficulty (0.85 + normalized structural score)
× verification
× efficiency
× modest novelty
× duplicate eligibility
× reward eligibility
```

Difficulty uses meaningful nodes, critical-path depth, independently necessary branches, technical domains, mutation risk, and verification structure. Token count, tool calls, message count, retries, and elapsed duration never increase difficulty by themselves.

Verification ranges from minimal unverified credit through independent verification. Replans reduce efficiency modestly; valid Trusted Skill reuse increases it. Frontier inference is neutral: appropriate use is neither punished nor rewarded. `progression reward <mission-id>` prints every factor and the rules version.
