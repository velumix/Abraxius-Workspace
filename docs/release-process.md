# Release process

1. Change `AbraxiusVersion` in `build/Version.props` and update human release notes.
2. Run `dotnet restore`, `dotnet build Abraxius.sln -c Release`, and `dotnet test Abraxius.sln -c Release`.
3. Create a protected tag matching the canonical version, for example `v0.1.0`.
4. GitHub Actions validates the tag, runs tests, publishes native desktop RIDs, and publishes the
   browser artifact.
5. Package contents are checked for secrets and hashes are generated. Protected signing,
   notarization, and artifact attestations run before publication.
6. The workflow creates a draft release, validates assets and `release-manifest.json`, then
   publishes only after all required jobs pass.
7. Run the isolated previous-version → candidate-version install test before calling Stable ready.

Local Linux packaging can be reproduced with:

```bash
DOTNET_EXE=/home/velumix/.dotnet/dotnet ./packaging/build-desktop.sh linux-x64 0.1.0 stable
./packaging/verify-artifacts.sh artifacts/releases/linux-x64
```

The release workflow is the source of production artifacts. It must not be run from pull-request
code with production signing or release-write credentials.
