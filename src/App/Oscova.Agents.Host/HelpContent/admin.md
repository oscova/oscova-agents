# Administration

The user directory groups email beneath the user identity. Roles, PIN status, and actions stay visible, with labelled groups on narrow screens.

Administration controls sign-in and user access for the installation.

> **In plain terms:** this is the access-control screen for the whole installation.

## Set installation access

- Choose **Standard** for username and password sign-in.
- Choose **Home** for profile selection followed by PIN sign-in.
- Home mode needs a PIN on the operator account.
- Password sign-in remains available.
- Wrong password and PIN attempts share a wait: two immediate retries, then 5, 10, 20, 40, and at most 60 seconds. The countdown applies to correct credentials too.
- Blocked attempts do not extend the wait. Successful sign-in or 15 minutes without an evaluated wrong credential resets it. Existing sessions stay open.
- Select **Manage providers** to configure the shared apps for active account providers, including Google Workspace and Microsoft Graph when their plugins are active.
- Select **Manage GitHub token** to open the optional token settings for skill search.

## Manage users

- Select **Create user** in **User Directory** to open the account form.
- The installation has one **Operator**, whose role cannot be changed here.
- Use a row's edit button to open profile, password, and PIN controls.

- Delete an account after confirmation, including its entire user folder, whether its vault is locked or has never been configured.
- Deletion stops running conversations and tasks. A busy database preserves the account for retry. A cleanup warning means the account was deleted but some files remain, see server logs.

> **Note:** The operator cannot delete their own account from Administration.

- Shared stored secrets are protected by the operator who saves them. They are unavailable after restart until the operator unlocks.
- Deleting an account also deletes shared secrets protected by that account's key.
- Vault Password locking does not pause ordinary scheduled tasks. Work that requires a locked vault is skipped or stays unavailable.

## Related

- [Your Profile](profile.md) - change your own password and picture.
- [Create user](dialog.create-user.md) - help for this dialog.
- [Edit user](dialog.edit-user.md) - help for this dialog.
- [Connected-account providers](dialog.oauth-providers.md) - help for this dialog.
- [GitHub skill search token](dialog.git-hub-skill-search.md) - help for this dialog.
