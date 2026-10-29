# Vault Password

Your Vault Password is separate from your Account Password. It unlocks saved credentials, agent and task definitions, system prompts, user settings, personal context, encrypted files, memory, private notes, chats and task runs.

> **In plain terms:** your Account Password signs you in, while your Vault Password opens the protected information Oscova keeps for you.

## Create or unlock

- Enter 12 to 256 characters in **Vault Password**.
- When creating it, repeat the same value in **Confirm Vault Password**.
- Select **Create vault** to protect your data and continue.
- After a restart or restore, enter the saved password and select **Unlock**.

> **Note:** Save this password safely. Oscova cannot recover it if you forget it.

If the password is accepted but protected data cannot open, keep your files and restore a compatible backup or application.

After a Host restart, sign in and enter your Vault Password before the dashboard or your files can open. A shared agent is available only after its owner unlocks their vault.

> **Note:** After two wrong guesses, waits increase from 5 to 10, 20, 40 and at most 60 seconds. Wait for the countdown, then retry manually. Correct passwords also wait. Refreshing, signing in again or restarting does not clear escalation. Blocked retries do not extend it. Correct vault verification or 15 minutes without an evaluated wrong guess resets it. Account sign-in has a separate counter.

## Change it later

- Open Profile and select **Change Vault Password**.
- Changing the password keeps your existing protected data available.
- Wrong current passwords share the same wait as **Unlock**.
- Logging out ends your browser session but does not lock an already unlocked vault.

## Related

- [Your Profile](profile.md) - manage your account and duress credentials.
- [Credentials](settings.credentials.md) - save API keys and tokens securely.
