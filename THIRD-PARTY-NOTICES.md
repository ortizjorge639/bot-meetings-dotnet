# Third-party notices

This repository is derived from the Microsoft Teams Bot Meetings sample in the [Microsoft Teams Samples repository](https://github.com/OfficeDev/Microsoft-Teams-Samples) and retains the MIT license and Microsoft copyright notice.

The application uses Microsoft and third-party packages restored from NuGet, plus GitHub Actions. Those components are not relicensed by this repository; each remains subject to its own license and notices.

Before distributing a build:

1. Restore from the committed lock files.
2. Generate a complete direct and transitive dependency inventory.
3. Review package repository and license metadata and include required texts or notices in the release artifact.
4. Run vulnerability and secret scanning.
5. Produce and retain an SBOM for the released commit.

The primary direct dependencies are listed in the project files. This file is a release-process notice, not a legal determination that every transitive obligation has been satisfied.
