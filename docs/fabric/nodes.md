# Nodes

`FabricNodeId` and certificate fingerprint identify a node. Hostname, IP, display name, and path do not. Roles describe Coordinator, Worker, ControlClient, ArtifactHost, ModelHost, or EvaluationWorker responsibilities and never grant trust.

Workers advertise observed platform, capabilities, models, sandboxes, resources, repositories, Artifact hashes, power, and connectivity. A worker has final admission control.
