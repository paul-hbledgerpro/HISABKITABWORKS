# HISAB KITAB WORKS 1.0.176

This release includes Record Shift Drop, background retrieval of a missing batch, automatic daily payout totals, colored over/short results, Light/Dark/Follow Windows appearance, cash ledger month closing and history, and monthly opening cash. See ledger-workflow-v175.md for the accounting workflow introduced in the test build.

## What's New on first launch

After installing this version, the normal interactive application launch displays the new features with an OK button at the bottom. Pressing OK persists an acknowledgement for this version in the Windows user's application-data folder. Restarting, switching stores, or reinstalling the same version does not show it again. Each later version gets its own notice; background sync never displays or consumes it. The notice works without a portal connection. If the process is interrupted before OK, it remains available on the next launch.

Release notes are bundled in `ReleaseHighlights.json`; update its Version and Features for every future release. The production packaging script rejects a version mismatch or an empty feature list.

## Validation

- WinForms Release build passes.
- Persistent acknowledgement, another version, reinstalls, concurrent launches, interrupted notice, separate Windows profiles and invalid version paths are covered by seven additional automated cases.
- The actual notice dialog is rendered offscreen in Light and Dark, with the version, feature content and bottom OK button checked. No production desktop application is launched.
- Existing ledger, sync and portal tests remain part of release verification.

Users receive this release through the existing update check on normal application startup or Help → Check for Updates. Publication makes the update available; it cannot install on PCs that are offline or not running the application. All Windows accounts using the same store database should update.
