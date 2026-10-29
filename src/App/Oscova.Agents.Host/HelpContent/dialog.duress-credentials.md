# Duress credentials

Set an alternate sign-in credential that permanently deletes your selected private data.

## Choose data

- Saved choices are never displayed, and this form starts empty.
- Choose conversations, private agents, tasks, or workspace contents to delete.
- The default workspace cannot be selected.
- Selected workspace folders remain after their contents are deleted.
- The operator can also empty the shared folder for every user.
- On Windows, emptying the shared folder skips files another program holds open.

## Set credentials

- Confirm your **Account password**.
- Enter a different **Duress password** of 12 to 256 characters and matching confirmation.
- An optional duress PIN needs matching 6 to 64 digit entries and an existing ordinary sign-in PIN.
- The duress PIN must differ from your ordinary PIN.
- Leave both PIN fields blank to remove the duress PIN.
- Two-factor authentication prevents duress sign-in.
- **Save duress credentials** replaces both credentials and every deletion choice.
- **Cancel** preserves the saved policy.

> **Note:** Deletion occurs during duress sign-in after vault unlock. After restart, nothing is deleted until you unlock the vault.

> **Note:** Deletion is permanent. Keep an earlier backup. Other sessions remain open, and retained agents or tasks may create files again.

> **Note:** Unselected data, stored credentials, tokens, and the vault remain. Already-deleted targets are skipped, while a deletion failure refuses sign-in.

> **Note:** Clearing the ordinary PIN suspends PIN sign-in. Account-password changes preserve duress credentials. A damaged duress record needs an operator password reset.

## Related

- [Your Profile](profile.md) - return to the parent screen's help.
