# Minimal build fixtures

`inert-apps-v1.zip` contains only six fixed synthetic fixtures extracted from the previously validated interactive v1 package:

- A target console executable and a distinct allowed control executable, built from `lab/AppLockerIdentityFixture.cs`.
- An inert timer window executable, built from `lab/InteractiveDemo.cs`.
- The fixed fixture identity contract, SIP hash helper and Windows API source required by the beta bridge build.

These are build inputs, not an installer. They contain no personal rule database, browser profile or diagnostic data. Source equivalents are present under `lab`; the frozen binaries keep existing identity-based tests reproducible.

Original package SHA256: `62e74c2ab5f7cb31685450780653d5f73da95371d1edef8ab8ff6b7c58ef38e4`.

Minimal archive SHA256: `5eea58c9b6c67c1e8301c9cab8c92e69218da6d1c64f3e576619cbd8f3142ea1`.

`scripts/Build-Beta.ps1` verifies the archive before extracting its fixed entries. If the original local package is absent it uses this archive; `-SourceFixtures` explicitly exercises this path without removing any local releases.
