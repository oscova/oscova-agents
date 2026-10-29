# Configure outbound hook

Choose an event and the command or HTTP endpoint it should call.

> **In plain terms:** a hook runs an action when an Oscova event occurs.

- Enter **Name**, choose **Handler type**, and select **Events**.
- **Enable this hook** controls whether it participates.
- Set **Timeout seconds** between 1 and 300.
- For **Command**, enter the command, optional working directory, and arguments one per line.
- For **HTTP**, enter the URL and any static or saved-secret headers.
- **Fail open** allows work after transport or execution failure.
- Explicit denial still blocks a blocking event.
- **Save hook** returns the edits to the parent page.
- Click **Save hooks** on that page to persist them.

> **Note:** Command hooks require the operator and run with the server's permissions. Trust the command and destination.

## Related

- [Hooks](settings.hooks.md) - return to the parent screen's help.
