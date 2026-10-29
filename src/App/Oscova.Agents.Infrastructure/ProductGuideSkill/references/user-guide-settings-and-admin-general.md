# General settings

General settings control your day-to-day experience in Oscova Agents: achievements, chat titles, recent-chat resume, follow-up behavior, low-battery protection, time zone, language, the About area and conversation cleanup. The separate **Customization** panel owns theme, layout and wallpaper.



> **Note:**
> General settings save to your account as you change them. Customization layout choices save in the current browser, while the selected wallpaper saves to your account.

## Appearance and wallpapers

Open **Customization** from the palette icon near the bottom of the navigation rail. Theme, sidebar, font, text direction, fullscreen and wallpaper-effects choices apply immediately and are stored in the current browser.

- Choose **Light**, **Dark** or **System**. A fresh browser starts with **System**, which follows your device's appearance and updates when it changes. Light and Dark stay selected until you choose another theme.
- The separate **Sidebar** Light/Dark choice is available only with the **Light** theme. Font, direction, fullscreen and wallpaper controls work with all three themes.
- **Reset** restores browser-local customization defaults, including the **System** theme, without touching your saved wallpaper.

To change the wallpaper:

1. Select **Change wallpaper**.
2. Choose a built-in image or upload a PNG, JPEG or WebP file up to 10 MB. The wallpaper saves immediately to your account.
3. Use **Wallpaper and glass effects** to switch between the wallpaper with translucent surfaces and a solid theme. Turning it off keeps the selected image, so turning it back on restores that wallpaper.

Accounts without a saved wallpaper start with the default built-in image. For best results use a 1920 by 1080 image. The wallpaper fills the screen without stretching, so its outer edges may be cropped on displays with a different shape. If the gallery cannot load, close and reopen it after checking the connection. A rejected file leaves the current wallpaper unchanged.

## Achievements and XP

**Achievements and XP** controls the complete achievements experience for your account. Turn it off to hide the Achievements profile-menu entry and navigation award icon, redirect the achievements page to chat and silence new unlock notifications.

Your dashboard activity is unchanged and no permanent achievement record is stored. Turning the setting back on restores your current badges and XP without replaying suppressed notifications.

## Low battery protection

**Skip scheduled tasks on low battery** is off by default. Enable it under **Settings > Preferences > General** to protect automatic scheduled and chained starts on the computer running Oscova.

- Set **Battery threshold (%)** to a whole number from 1 through 100. The default is 10.
- Oscova skips an automatic start only when a system battery is detected, its charge is below the threshold and external power is confirmed disconnected. A charge equal to the threshold is allowed.
- The facts refer to the server computer even when you open Oscova from another device. If no battery is detected or battery or power information is unavailable, the control is disabled with an explanation beside it.
- Each skipped occurrence is recorded as **Skipped** with a low-battery reason and an inbox notification linked to the task and run. It does not start chained tasks or stop a running task, and plugging in does not retry it. Recurring tasks keep their next normal occurrence, **Run now** retries manually, a skipped one-shot needs manual execution and webhook starts stay available.
- If power information becomes unknown after protection was enabled, the preference stays enabled and automatic starts are permitted until known facts establish the skip condition.

## Chat titles and resume behavior

**Generate chat titles** lets Oscova use your selected chat model to rename conversations after a few messages. Leave it off if you prefer manually named or timestamped conversations, or if your model should not spend extra tokens on titles.

**Resume recent chats** controls whether returning to chat opens your latest conversation or the start screen. Enter a whole number of minutes, `0` or more, for how old your last message can be and still count as recent. The default is `10`. Set it to `0` when you always want the start screen.

## Follow-up behavior

**Follow-up behavior** saves what Enter does while an agent is working. **Queue** is the default and runs text as separate turns after the current response finishes, in the order you sent it. **Steer** joins the current response at its next model call. Ctrl+Enter uses the other choice for one message.

- The composer keeps **Stop** beside Send while you type, including during an approval wait. Stop preserves the draft and pauses queued messages. A failure also pauses the queue, which resumes after a later completed web turn. Channel turns do not drain it.
- Use a queued row's **Steer**, **Delete** or **More actions > Edit message** above the composer. Editing removes the row and returns its text if it is still queued. A message that has already started is not returned.
- Denied follow-ups and unapplied steering text return to their conversation's composer without replacing a newer draft. A denied queued Steer keeps the item in place.
- Follow-ups accept text only. Queues are lost when Oscova restarts, and pending composer recovery lasts while the chat page stays open. Connected channels are unaffected.

See [Main agent chat](references/user-guide-chat-and-agents-main-agent-chat.md).

## Notification delivery and sounds

Notification delivery and sound effects live in **Settings > Preferences > Notifications**: pause bell notifications, choose task notification behavior, show log warnings, show admin-only system errors and control short UI sound cues.

## Time zone and language

**Time Zone** controls how timestamps appear throughout the UI. Choose **Server Default** for the server's local time zone or search for a specific zone.

**Language** changes the UI culture. Choose English, Spanish, Simplified Chinese, Hindi, Arabic, French, Bengali, Brazilian Portuguese, Indonesian or Urdu, each shown in its native name. The same languages are available during first-time setup.

- Oscova reloads the page after saving a different language so server-rendered and client-rendered text use the same culture.
- Changing the language also sets the matching text direction before the reload. Arabic and Urdu use right to left, and the other languages use left to right. The direction saves in the current browser, and you can override it afterward under **Customization > Direction**.

> **Note:**
> UI language changes do not rewrite saved agent instructions, prompts, file contents or chat history.

## About

**About** shows the Oscova Agents host version and the reusable framework version. Include both when reporting a bug or comparing installations.

**Credits and licenses** lists the third-party software included with Oscova. Expand a component to read its complete bundled license or notice text. The content is stored with the application, works offline and contains no external links.

## Delete conversations

**Delete Conversations** removes all saved conversations for your account after confirmation. The button is disabled when there are no saved conversations. Agents, tasks, files, credentials, providers, settings and memory files stay, and recent-chat resume has nothing to reopen until you create or open another thread.

> **Caution:**
> Conversation deletion cannot be undone from the UI. Export a backup first if history matters.

## Troubleshooting

- If a setting shows an error toast, retry after the connection recovers. The previous saved value stays in effect.
- If a language change does not appear everywhere, reload the page.
- If timestamps look wrong, choose the intended time zone instead of **Server Default**.
- If **Delete Conversations** is disabled, the account has no saved conversations.

## Related articles

- [Notifications](references/user-guide-settings-and-admin-notifications.md)
- [Files](references/user-guide-knowledge-and-files-files.md)
- [Profile](references/user-guide-settings-and-admin-profile.md)
- [Main agent chat](references/user-guide-chat-and-agents-main-agent-chat.md)
