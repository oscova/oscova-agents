# Users, roles and PIN

The Administration screen is where the operator creates users, switches the sign-in mode, sets PINs and manages shared secrets such as OAuth providers and the GitHub token.

> **Note:**
> The installation has one fixed **Operator**. Accounts created in Administration receive **User** access. A PIN is a 6 to 64 digit sign-in code used by Home authentication mode.

## Operator and user access

- **Operator** can open Administration and use restricted operations, including network, plugins, hooks, shared avatar settings and environment backup export.
- **User** has normal access to personal chat, agents, model and speech providers, credentials, files and other per-user settings.

Administration shows each account's access as a read-only badge. The operator role cannot be changed, and the screen offers no way to create another operator or delete the operator's own account. These guardrails protect recovery access.

The operator controls execution permissions for regular users, including **Projects**, **Workspace shell**, **Project shell** and **Shared folder**. New users start with these enabled, and each can be changed independently in the user editor. **Shared folder** gives the user and their agents the [shared folder](references/user-guide-knowledge-and-files-shared-folder.md). Server workspace file tools stay usable. Revoking a permission denies new affected work and cancels affected running calls and managed jobs. See [Device nodes](references/user-guide-getting-started-device-nodes.md) for laptop projects.

## Authentication mode

Oscova supports two sign-in modes:

- **Standard**: users sign in with username and password.
- **Home**: users choose a profile, then sign in with a PIN.

Select **Save auth settings** after choosing a mode. To enable Home mode, the operator must have a PIN. Profiles without a PIN stay visible but cannot use profile sign-in.

The login screen still offers **Password sign-in**. Despite the `/login/admin` route name, that form accepts the username and password of any valid user.

## Create a user

1. Select **Create user** in the **User Directory** header.
2. Fill in **Full name**, **Username**, **Email**, **Password** and **Confirm password**.
3. Select **Create user** in the dialog. The dialog closes after success and the directory refreshes.

Rules:

- Usernames use lowercase and must be at least 3 characters. Uppercase letters become lowercase as you type.
- Passwords must be at least 6 characters, and the confirmation must match.
- Usernames and email addresses must be unique.
- Every new account receives **User** access.

The operator sets only the Account Password. Each user creates their own Vault Password after first sign-in, before the rest of Oscova loads. The operator never chooses it for them.

## Manage existing users

The **User Directory** table lists every non-system account with email, roles and PIN status. Use a row's edit button to open its user dialog, which keeps Profile, Password Reset and PIN Login in stacked sections:

- **Save profile** updates username, email and full name.
- **Reset password** sets a new password for another account.
- **Set PIN** or **Change PIN** saves a new PIN.
- **Clear PIN** removes a saved PIN.
- **Delete** removes the account after confirmation.

The dialog stays open after successful changes and refreshes the directory when you close it. The operator cannot delete their own account or reset their own password here. Use [Profile](references/user-guide-settings-and-admin-profile.md) to change your own password.

### PIN rules

- PINs contain 6 to 64 digits using only 0 to 9. Leading zeros are preserved, and the confirmation must match exactly.
- Home mode prevents clearing the operator PIN needed for profile sign-in. Switch to Standard mode before removing it.
- PIN sign-in is unavailable when two-factor authentication is enabled. Password sign-in stays available from the login screen.
- Older 4 to 5 digit PINs must be replaced by the operator.
- Users configure optional duress credentials in [Profile](references/user-guide-settings-and-admin-profile.md). Administration does not show duress status. Clearing an ordinary PIN suspends both ordinary and duress PIN sign-in.

### Sign-in retry waits

Password and PIN attempts share an account wait. The first two wrong attempts permit immediate retry. Further failures require 5, 10, 20, 40 and then at most 60 seconds, and the form shows the remaining time. Correct credentials must also wait, including duress credentials.

Refreshing, switching methods, changing credentials or restarting the host does not clear an active wait, and blocked attempts do not extend it. A completed sign-in resets escalation, as does 15 minutes without an evaluated wrong credential. Other signed-in sessions stay usable.

### Delete a user

The operator can delete a regular user whether their Vault Password is configured, locked or unlocked. Deletion does not need the user's Vault Password or read their protected content.

- Deletion stops and waits for the user's running conversations and scheduled tasks, then removes the account and the entire user folder, including memory, credentials and files. Existing sign-in sessions lose access.
- If the database is busy, the account stays and the error tells you to wait for other activity to finish. Running work may already have stopped.
- If the account is deleted but some files cannot be removed, a warning tells you to check server logs for the cause and remaining files.
- Deletion also removes shared stored secrets protected by that user's key and forgets the user's unlocked key immediately. Operator-owned shared secrets become available after restart only when the operator unlocks.

## GitHub Skill Search token

The **GitHub Skill Search** summary stores an optional **Personal Access Token** for the whole host. Oscova uses it when users select **Find skills online** on the Skills page: the default GitHub API search uses it for repository metadata, selected descriptions and installation files, and full local repository downloads under **Skills > Manage sources** use it too. The search method and repository list are configured there, while this dialog manages the shared credential.

> **Note:**
> A Personal Access Token, often shortened to PAT, is a GitHub-generated secret that lets an app make authenticated GitHub API requests without storing your GitHub password.

Without a token, GitHub's anonymous API limit is low. A token gives skill search an authenticated allowance, which helps on shared installations. GitHub applies its own rate limits.

1. Open **Administration** and select **Manage GitHub token**.
2. Paste the token into **Personal Access Token**.
3. Select **Save**.

The status badge changes between **Not configured** and **Configured**. When a token is already configured, leave the field blank and select **Save** to keep it. **Clear** removes the saved token after confirmation.

## OAuth providers

The **OAuth Providers** summary shows whether Google and Microsoft are configured. Select **Manage providers** to open their shared settings. Each provider has:

- **Client ID**: clearing this disables the provider.
- **Client Secret**: required the first time you save a provider. Leave it blank when editing to keep the stored secret.
- **Configured** or **Not configured** status.

Save Google and Microsoft separately. The dialog stays open after each save.

The GitHub token and OAuth client secrets are protected by the operator's Vault Password. Create or unlock it before saving secrets. After a Host restart, the operator must unlock again before features can use these shared secrets.

> **Note:**
> These are system-level OAuth app credentials. Individual users still connect or disconnect their own Google or Microsoft accounts from **Settings > Accounts & Access > Connected Accounts**.

## Troubleshooting

- If Home mode will not save, set a PIN on the operator account first.
- If a visible profile cannot sign in, ask the operator to set its PIN or use **Password sign-in**.
- If a connected account provider appears unavailable, confirm its OAuth provider dialog has both a client ID and stored client secret.
- If a user cannot be deleted, check whether it is the operator's own account.
- If **Clear PIN** is disabled, that user does not have a PIN.
- If PIN save fails, enter 6 to 64 digits in both PIN fields.

## Related articles

- [Operator setup](references/user-guide-settings-and-admin-operator-setup.md)
- [Profile](references/user-guide-settings-and-admin-profile.md)
- [General settings](references/user-guide-settings-and-admin-general.md)
- [Skills](references/user-guide-knowledge-and-files-skills.md)
- [Connected accounts](references/user-guide-connections-connected-accounts.md)
