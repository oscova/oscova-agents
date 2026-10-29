# Logs

The logs dialog shows the Oscova Agents application log files so you can diagnose product, agent, integration and background-task issues.

> **Note:**
> A log entry is a timestamped record written by the application. It can include severity, logger name, event name, message, metadata and exception details.

## Open and filter logs

Open **Application Logs** from the profile menu, then narrow the entries:

- **Scope:** admins can choose **Mine**, **System** or **User...**.
- **User ID:** for **User...** scope, enter a valid GUID and press **Enter**.
- **Level:** choose **All Levels**, **Trace**, **Debug**, **Info**, **Warn**, **Error** or **Fatal**.
- **Log file:** choose the structured log archive to inspect.
- **Search logs...:** type a term and press **Enter**.
- **Refresh:** reload available files and entries from disk.



> **Note:**
> A GUID is a long unique identifier, such as a user ID. The User scope accepts only valid GUID text.

Level filtering is hierarchical. **Info** includes Info, Warn, Error and Fatal entries, and **Warn** includes Warn, Error and Fatal entries.

## Read entries

The table shows time, level, logger and message. Warning rows are lightly highlighted, and error and fatal rows receive stronger highlighting.

- Expand a row with metadata or exceptions. Metadata shows structured key-value details. Exceptions show the exception type, message and stack trace when available.
- The dialog reads 100 entries per page by default. Use the page buttons when more results match.
- Oscova Agents reads the newest 50,000 valid entries from a selected file before filtering and paging.
- Oscova keeps daily JSONL streams and retains up to 14 archive files for each user or system stream.
- The dialog does not provide a copy action.

## Export logs

Use **Export** after choosing a log file:

- **Original JSON Lines** downloads the complete selected file and ignores the active filters.
- **Filtered text** exports all matches from the newest 50,000 valid entries in a readable format.
- **Filtered CSV** exports the same matches with stable columns for spreadsheet tools.

Filtered exports use the active level and search filters. They are not limited to the current 100-entry page.

## Clear a log file

Select **Clear** to erase the selected log file after confirmation. The button is disabled while logs are loading or when no file is available.

> **Caution:**
> Clearing a log file cannot be undone. Use **Export > Original JSON Lines** before clearing a file you need to preserve.

## Privacy and sharing

Logs may include provider names, URLs, user IDs, file names, integration context, exception details and other sensitive values. Review every log before sharing it outside your team.

## Troubleshooting

- Check logs after model provider test failures.
- Check logs when tool servers fail to reconnect.
- Check logs after failed Telegram, Discord or Avatar Engine actions.
- For agent task failures, inspect the task run first, then use logs for lower-level errors.
- If no log files appear, perform the failing action again and select **Refresh**.
- If an older entry is missing from a very large log file, it may be outside the newest 50,000 valid entries read by the dialog.
- If User scope fails, confirm the user ID is a GUID and that you are signed in as an admin.

## Related articles

- [Monitoring runs](references/user-guide-automation-monitoring-runs.md)
- [MCP servers](references/user-guide-connections-mcp-servers.md)
- [Network](references/user-guide-settings-and-admin-network.md)
