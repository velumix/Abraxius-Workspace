# Evaluation Lab UI

Evaluation Lab is a first-class rail destination with Overview, Suites, Runs, Models, Specialists, Skills, Retrieval, Security, and Regressions navigation. Secondary navigation wraps at constrained widths. Suite/run/regression and metric lists use Avalonia virtualizing collection controls and compiled bindings.

Store queries, suite execution, comparisons, artifact creation, and mission creation are asynchronous and never run on the UI thread. The UI displays numbers, environment compatibility, and gate explanations rather than magic red/green state. Raw trajectories/artifacts load only when selected.
