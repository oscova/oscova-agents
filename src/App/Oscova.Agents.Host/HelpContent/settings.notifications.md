# Notifications

Control bell and inbox entries, review notifications, and choose which sound cues play.

Use the **Notification delivery** group for messages and the **Sound effects** group for volume and individual cues.

> **In plain terms:** notifications are app messages that tell you something finished, failed, or needs attention.

## Delivery and inbox

- **Mute all** pauses live bell alerts. New notifications remain in the app inbox.
- Task failures and skipped runs always appear in the app inbox, except Vector Indexing runs skipped for a missing embedding provider, which stay in the task's run history. Each task's **Notify on completion** toggle controls its additional success notice.
- Toggle **Show log warnings** to surface warning messages.
- Admins can toggle **Show system errors**.
- Normal-priority notifications show their title in a toast. Low-priority notifications appear in the inbox without a toast.
- A task skipped for a missing chat model provider uses low priority. Its entry still says **Needs attention**.
- Close the toast at any time without removing its bell entry.
- Untouched toasts disappear after four seconds.
- Open the bell to filter entries, mark all read, clear them, or open notification settings.
- **View all** opens the larger inbox.

> **Note:** delivery controls are disabled while notifications are muted.

## Sounds and recovery

- Toggle **Play UI sounds** to turn short audio cues on or off.
- Set **Volume** for every cue at once.
- Turn individual **Sound cues** on or off.
- If a save or inbox load fails, reload and retry after the connection recovers.

> **Note:** silent cues may also mean browser or operating-system audio is muted.

## Related

- [General](settings.general.md) - set language, time zone, profile display, and cleanup options.
- [Notification inbox](dialog.notification-inbox.md) - help for this dialog.
- [Notification details](dialog.notification-details.md) - help for this dialog.
