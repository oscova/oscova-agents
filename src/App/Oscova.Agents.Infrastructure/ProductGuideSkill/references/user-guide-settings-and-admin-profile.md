# Profile

Profile is your own account record in Oscova Agents: full name, preferred name, email address, Account Password, Vault Password, duress credentials and profile picture. Open your user menu and select **Account**. Every signed-in user can edit their own profile, and your username is shown but not editable here.

> **Note:**
> Admin user management is separate and cannot reset your own password from the Administration screen.

## Account details

1. Edit **Full name**, **Preferred name** and **Email address**.
2. Select **Save changes**.

**Preferred name** tells agents what to call you when their instructions use your nickname. The email address must be valid and cannot already belong to another user. A clear full name helps other users recognize you in admin screens, profile menus and shared-agent context.

## Change password

1. Enter **Current password**.
2. Enter **New password** and repeat it in **Confirm new password**.
3. Select **Save changes**.

Leave every password field blank when you only change your name or email. The new password and confirmation must match exactly, and a wrong current password rejects the save. Account fields are processed before the password change, so if a password error appears after you also edited your name or email, close and reopen the dialog to confirm which details saved before retrying the password.

## Vault Password

The **Vault Password** unlocks your encrypted data: stored credentials, agent and task definitions, system prompts, user settings, personal context, encrypted files, memory, private notes, chats and task runs. It is separate from your Account Password and must be 12 to 256 characters. You create it after first sign-in, before onboarding or other authenticated pages load.

> **Important:**
> Save the Vault Password somewhere safe. Oscova never stores it and cannot recover it.

To change it, choose **Change Vault Password**, enter the current password, then enter and confirm its replacement. Oscova keeps the same underlying data-encryption key, so existing protected data is not rewritten.

Each secret has one job: the Account Password signs in, the Vault Password unlocks encrypted data, the Export Passphrase protects one backup and the CLI bearer token authenticates terminal commands.

- Logging out ends only your browser session. The unlocked key stays in the running Host, so credential-backed background work continues. A Host restart clears all unlocked keys and invalidates old browser and CLI sessions. Sign in again and enter the Vault Password once to make your encrypted data and enabled bots available.
- An imported environment keeps each user's wrapped encryption key. Each user enters their own Vault Password after restore before Oscova can open their protected credentials, chats or task runs.
- If Oscova accepts the Vault Password but cannot open protected data, access stays locked. Keep the original files and restore a compatible backup or use the application version that supports them. Resetting the password does not repair incompatible ciphertext.

### Wrong-password waits

**Unlock** and the current-password field in **Change Vault Password** share one counter. The first two wrong guesses cause no wait. The third starts a five-second countdown, and further wrong guesses increase the wait to 10, 20, 40 and at most 60 seconds. During a wait the action is disabled and even a correct password is refused. When the countdown ends, enter the password and select the action yourself.

Refreshing, reopening the dialog, signing out and back in, or restarting the Host preserves the escalation. Submissions during a wait neither increase the counter nor extend its deadline. A successful verification resets it. Otherwise, the next wrong guess at least 15 minutes after the last evaluated wrong guess starts again at the first failure. Account Password and Home PIN sign-in share a separate counter, so success at one gate does not reset the other.

## Duress credentials

A **duress password** or **duress PIN** opens an ordinary-looking sign-in and runs your saved deletion selection. If your vault is already unlocked in the running Host, deletion runs immediately at sign-in. If the Host restarted and the vault is locked, enter the Vault Password at the normal gate and deletion runs before the dashboard and files open. If you never unlock the vault, nothing is deleted and your application data stays inaccessible. If deletion fails during vault unlock, the vault stays locked and the application does not open.

> **Important:**
> Deletion is permanent. Recovery requires an earlier backup. Test only with disposable data.

1. Choose **Set duress credentials** in Profile.
2. Enter your Account Password and a separate duress password of 12 to 256 characters, with matching confirmation.
3. Optionally enter and confirm a duress PIN of 6 to 64 digits using only 0 to 9. Leading zeros count. The operator must first assign your ordinary sign-in PIN, and the two PINs must differ.
4. Choose any optional targets and save.

Targets:

- **Delete all conversations** removes every conversation and its related stored data.
- Selected custom agents are deleted. Shared agents cannot be selected.
- Selected tasks are deleted. Deleting an agent alone does not delete its tasks.
- Selected workspaces are emptied, including any encrypted files inside. The workspace stays available, and the default workspace is never cleared.
- The operator can also select **Empty the shared folder for every user**. It deletes the [shared folder](references/user-guide-knowledge-and-files-shared-folder.md) contents for every member and never blocks the sign-in. On Windows, it skips files another program holds open.

Rules:

- Setup is available in either sign-in mode, but PIN sign-in requires Home mode. Two-factor-enabled accounts cannot configure or use duress credentials or PIN sign-in.
- Both duress credentials use the same complete deletion selection. Vault data, stored credentials, tokens and unselected data remain.
- The form always starts empty and never shows saved status or previous choices. Saving replaces both duress credentials and the entire selection. Leave both PIN fields empty to remove a previously configured duress PIN. The duress password stays required.
- Enter the duress password on the password sign-in screen, or the duress PIN on your Home profile's PIN screen. Clearing your ordinary PIN suspends both PIN sign-ins, and assigning a different ordinary PIN restores them. Account-password changes and operator resets preserve both duress credentials.
- A selected agent, task or workspace that is later deleted or renamed is already handled. Retained agents or tasks can create new files in a cleared workspace later. If deletion fails, sign-in is refused and a later retry can finish the remaining work.
- Wrong password and PIN attempts share one wait: two immediate retries, then 5, 10, 20, 40 and at most 60 seconds. Correct ordinary and duress credentials also wait. Refreshing or switching sign-in methods does not bypass it, and blocked submissions do not extend it. A completed sign-in or 15 minutes without an evaluated wrong credential resets the escalation. Existing sessions stay open.

The Account Password protects the integrity of the saved deletion selection. Missing or damaged records block ordinary password sign-in, so ask the operator to reset the account password to recover access. Ordinary PIN sign-in has no such tamper protection. A copied encrypted record can be attacked offline, a guessable duress PIN can reveal its configuration and deletion choices, and the online wait does not protect against offline guessing.

## Profile picture

1. Select **Change profile picture**.
2. Choose a built-in picture or upload your own PNG, JPEG or WebP file up to 2 MB.
3. Select **Use this picture**.

The gallery shows the current and selected picture before you apply it and saves on its own. **Save changes** applies only to account details and password fields. Oscova validates the first bytes of an upload, not just the filename, so renaming an unsupported file to `.png` is still rejected. A rejected upload leaves the current picture in place. For another error, close and reopen the gallery after the connection recovers, check the preview, then select the picture again.

## Troubleshooting

- If the image is rejected, check the format and 2 MB size limit.
- If the built-in gallery is empty, close and reopen it after checking the connection.
- If the picture does not change immediately, wait for the success toast, then refresh the page.
- If email save fails, another account may already use that address.
- If password save fails, re-enter the current password and matching new-password fields.
- If stored credentials remain unavailable after restart, reopen Oscova and enter the Vault Password when prompted.
- If normal sign-in reports invalid credentials after the duress record was edited or damaged, ask an administrator to reset the account password.

## Related articles

- [General settings](references/user-guide-settings-and-admin-general.md)
- [Users, roles and PIN](references/user-guide-settings-and-admin-users-roles-pin.md)
- [Operator setup](references/user-guide-settings-and-admin-operator-setup.md)
