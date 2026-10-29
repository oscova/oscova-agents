# Operator setup

Operator Setup is the first screen of a fresh installation. It appears only while Oscova Agents has no users, and it lets you create the installation's one operator or restore an encrypted backup before any account exists.

> **Note:**
> A bootstrap screen is a one-time setup screen. Operator Setup is rendered by the app shell only while the user database is empty. It is not a normal route you open later.

## Create the operator

1. Fill in the form:
   - **Full name:** the display name shown in the app.
   - **Preferred name:** the required name agents use when addressing you.
   - **Email:** a valid email address for the account.
   - **Username:** the sign-in name. It must be at least 3 characters and unique. Uppercase letters become lowercase as you type.
   - **Password:** at least 6 characters.
   - **Confirm password:** the same password again.
2. Select **Create operator**. Oscova assigns fixed **Operator** access, provisions the user's workspace files, records the account as the host owner, attempts to write the first local CLI token and sends you to the login page, which confirms the operator was created.
3. Sign in and create your separate Vault Password before onboarding begins. Oscova cannot recover this password, so save it somewhere safe.

> **Note:**
> The operator can manage users, backups, network settings and other restricted areas. The operator role cannot be changed in the normal Administration screen.

## Restore a backup instead

Select **Restore backup** if you already exported an Oscova environment backup. Restore requires:

- A `.oscb` backup file no larger than 500 MB.
- The exact passphrase used during export.
- A fresh installation with no users in the database.

After you select **Restore backup**, Oscova validates the backup, stages the restore, starts a replacement process and stops the current process. Keep the browser window open until the restart completes. If a terminal login flow was waiting during the restore, the restarted host asks for a fresh login.

> **Note:**
> A `.oscb` file is an encrypted Oscova Backup file. Its expanded contents are limited to 2 GB. It contains restorable app data, but not local models, voices or the shared system log folder. Per-user logs are included with user data.

## Troubleshooting

- **Passwords do not match:** re-enter both password fields.
- **Username or email already exists:** a user is already present. Sign in instead of using setup.
- **Only `.oscb` backup files are supported:** choose the exported backup file, not an extracted zip or database file.
- **Restore is only available on a fresh install:** remove or replace the existing app data before restoring.
- **Incorrect passphrase or corrupted backup:** confirm the passphrase and verify the backup file was not truncated.
- **Backup is too large:** use a `.oscb` file within the 500 MB upload limit.
- **Restart does not complete:** keep the browser open while it polls for the replacement host, then reopen the normal login page.

> **Important:**
> Do not delete production users just to show Operator Setup unless you have a verified backup and understand the data loss.

## Related articles

- [First-time setup](references/user-guide-getting-started-first-time-setup.md)
- [Users, roles and PIN](references/user-guide-settings-and-admin-users-roles-pin.md)
- [Profile](references/user-guide-settings-and-admin-profile.md)
