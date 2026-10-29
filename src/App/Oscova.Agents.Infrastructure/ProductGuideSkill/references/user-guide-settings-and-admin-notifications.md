# Notifications

Notifications control which app events reach the bell and inbox, and whether Oscova Agents plays short sounds for important UI moments. Open **Settings > Preferences > Notifications** to change these options. Changes save as you make them.



> **Note:**
> The bell is the notification icon in the navigation rail. The inbox is the saved list of notifications you can review later.

## Choose what reaches you

Use **Notification delivery** to decide which app events should interrupt you or be saved for later review.

- **Mute all** pauses live bell alerts while new notifications continue to be saved in the inbox. While it is on, the other delivery controls are disabled. Muting does not remove existing entries. Use the bell's clear action when you also want to empty the saved inbox.
- Task failures and skipped runs always send an app inbox notice, except the Vector Indexing skip for a missing embedding provider. Each task's **Notify on completion** toggle in the task editor controls its additional success notice.
- **Show log warnings** surfaces warning notifications generated from application logs.
- **Show system errors** appears for admins and controls system-level error notifications.

Automatic task outcome notices contain stock status text and link to task details. Task entries show the task's name, and skipped-run notices state the stock skip reason. Agent-initiated messages through `notification__notify` are separate. The agent picks the channel its instructions name, or the web app when none is named.

## Use the bell and inbox

The bell shows an unread marker while unread entries exist. Open it to review newest entries first and filter by **All** or **Tasks**. Admins also get a **System** tab.



- A normal-priority notification also shows a toast with informational, success, warning or error styling. Close it immediately or leave it to disappear after four seconds. Dismissing the toast does not remove the full message from the bell.
- A low-priority notification arrives in the bell and inbox without a toast. A task skipped because no chat model provider is configured uses low priority. The saved notice still says **Needs attention** and opens the skipped run with its setup reason. Other skipped-task notices keep their ordinary toast unless their handler lowers the priority. Embeddings are optional, so a Vector Indexing run skipped for a missing embedding provider is recorded only in the task's run history. Arrival sounds follow your sound and mute settings either way.
- Selecting an entry marks it as read. Chat entries open their conversation, task entries open the task and recorded run, and harness or Avatar release entries open current update details. Other entries open a detail dialog with the title, status, timestamp and message.
- Agent-initiated chat notifications show a generic notice. Open the linked conversation to read the private message, which is stored with your encrypted chats.
- **Mark all read** keeps entries without the unread marker. **Clear notifications** removes saved entries without a confirmation prompt. For an admin, both actions also include visible system entries.
- **View all** opens the larger inbox. It keeps the latest 50 entries for each user scope. Consecutive entries with the same kind, title, conversation link, task identifier and run identifier within one minute are combined, with the newest details retained. Notifications linking to different conversations, task runs or releases remain separate even when their titles match.

Harness and Avatar release checks run automatically at startup and every six hours. New releases notify the operator even when Avatar is absent from this computer. **Mute all** keeps these notices in the inbox while suppressing live alerts, and repeated checks and restarts do not announce the same release again. **Refresh** in the update dialog checks immediately. See [Update the installed application](references/user-guide-getting-started-command-line.md) for installation and restart steps.

## Sound effects

Use **Sound effects** to control short audio cues. A sound cue is a brief confirmation sound, not spoken audio.

- **Play UI sounds** turns all UI sound cues on or off. When it is off, the volume and individual cue controls are disabled.
- **Volume** sets one volume level for every cue.
- **Sound cues** lets you enable or disable **Send message**, **Enter chat**, **Response complete**, **Response error**, **Notification arrival**, **Starter prompt tap** and **Onboarding complete** individually.

## Practical setups

- **Quiet daily work:** turn on **Mute all** to pause live alerts and review saved notices in the inbox. Turn **Notify on completion** off on tasks whose success does not need a notice.
- **Unattended automation:** leave **Mute all** off and keep **Notify on completion** on for tasks whose successful runs need a notice. For messages on a connected channel, name the channel in the task's instructions and keep the channel running. Admins who operate the host may also want **Show system errors** on.

## Troubleshooting

- **Delivery controls are disabled:** turn off **Mute all**.
- **Volume or cue controls are disabled:** turn on **Play UI sounds**.
- **Show system errors is missing:** sign in with an admin account.
- **A success notice is missing:** check **Notify on completion** on the task. If live alerts are silent, check **Mute all** and review saved notices in the inbox. For agent-initiated messages, check that the instructions name the channel and that the channel is running.
- **The bell cannot load or a setting shows an error:** reload the page and retry after the connection recovers.
- **Cues stay silent:** confirm browser or operating-system audio is not muted.

## Related articles

- [General settings](references/user-guide-settings-and-admin-general.md)
- [Agent tasks](references/user-guide-automation-agent-tasks.md)
- [Monitoring runs](references/user-guide-automation-monitoring-runs.md)
- [Logs](references/user-guide-settings-and-admin-logs.md)
