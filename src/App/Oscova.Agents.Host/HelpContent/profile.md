# Your Profile

Update your own account details, preferred name, password, and profile picture from this dialog. Your username is not editable here.

## Account and password

- Edit **Full name** and **Email address**.
- Set **Preferred name** to choose what agents call you in instructions that use your nickname.
- Email must be valid and not already used.
- Leave password fields blank to keep your current password.
- Enter **Current password** before setting a **New password**.
- Repeat the new password in **Confirm new password**.
- Click **Save changes** to apply account and password edits.

> **Note:** after a password error, reopen the dialog to confirm whether separate name or email edits saved.

## Vault Password

- You create your **Vault Password** after first sign-in, before the rest of Oscova loads.
- In Profile, choose **Change Vault Password** to replace it.
- Wrong current passwords share the vault unlock countdown, with waits from 5 to at most 60 seconds.
- Wait until the countdown ends, then select **Change password** manually.
- Correct vault verification or 15 minutes without an evaluated wrong vault guess resets escalation.
- Account sign-in has a separate counter, so signing in again does not clear the vault wait.
- Enter the current password, then enter and confirm a new password of 12 to 256 characters.
- Save it somewhere safe. Oscova cannot recover a forgotten Vault Password.
- Changing it re-protects your encryption key, so existing encrypted data remains available.
- Logging out does not lock background work. After Oscova restarts, sign in and unlock the vault once.
- The Account Password signs you in. The Vault Password unlocks your encrypted data. The Export Passphrase protects one backup file, and the CLI token signs in the terminal. They are separate.
- Restoring a backup still requires each user's own Vault Password to open their protected data.
- Encrypted workspace files use the same key, so changing the password does not rewrite them.

## Duress credentials

- **Set duress credentials** opens the separate deletion-policy form.
- Review its help before saving an alternate sign-in credential.

## Picture and recovery

- **Change profile picture** opens the picture gallery.
- Picture changes save separately from **Save changes**.

## Related

- [Administration](admin.md) - manage other users if you are an admin.
- [Duress credentials](dialog.duress-credentials.md) - help for this dialog.
