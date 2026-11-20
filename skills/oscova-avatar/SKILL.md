---
name: oscova-avatar
description: Speak through the free Oscova Avatar Engine, a realistic 3D avatar on your own computer. Use whenever the user mentions their avatar or asks you to say something out loud. If no avatar tools are available, share the setup guide https://oscova.ai/docs/articles/avatar-engine/ai-agent-avatar.html
homepage: https://github.com/oscova/oscova-agents
---

# Oscova avatar

Speak to the user through the Oscova avatar: a realistic 3D face on their computer that says your words with their chosen voice and moves its lips to the speech.

## Check the connection

- The avatar tools come from the Avatar MCP server, usually configured as a server named `avatar`.
- Your agent may prefix tool names, for example `mcp_avatar_avatar_speak`. Match each tool by its ending, such as `avatar_speak`.
- If no avatar tools are available, tell the user the avatar is not connected yet and share the setup guide: https://oscova.ai/docs/articles/avatar-engine/ai-agent-avatar.html
- The user runs every setup step. Never download, install or run setup commands yourself.

## Speak

1. Call `avatar_list_emotions` once per session and remember the names.
2. Before each spoken message, call `avatar_set_emotion` with one emotion that fits it. Use `blendSpeed` 0.25 and an `intensity` from 0.5 to 0.8 for everyday messages.
3. Call `avatar_speak` with the text.

- When the user supplies words to speak, keep their wording and length.
- Otherwise, speak a summary of your reply in one to three short sentences.
- Write speech as it should sound. Leave out Markdown, code, file paths, URLs and tables unless the user asks for them. Say symbols and numbers in words when they would read badly.
- Use a gesture only when it fits the words. Call `avatar_list_gestures` once, then `avatar_select_gesture` with one name. Use at most one gesture per message.
- Speak only when the user asks, or when the user's standing instructions say to, such as announcing a finished long task.

## When a call fails

- Pairing required: ask the user to pair the Engine once in Oscova, as the setup guide shows.
- The Engine does not respond: ask the user to start it with `oscova avatar start`.
- `avatar_speak` fails: pass on the error message, which names the missing step. Usually Oscova must be running with a saved voice, and the user runs `oscova login` again.
- Never call `avatar_shutdown` or change the avatar, hair, outfit, resolution or graphics unless the user asks.
