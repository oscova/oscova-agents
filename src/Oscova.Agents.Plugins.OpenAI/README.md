# Oscova OpenAI

Optional OpenAI speech recognition and synthesis for Oscova Agents.

Install this plugin during interactive `oscova start`, or through Settings > Plugins and restart. Select OpenAI independently for recognition and synthesis in Settings > Speech. Both can reuse the same saved OpenAI credential used for models.

The host resolves the selected user's credential and supplies it to each speech operation. The plugin uses host-managed HTTP transport and stores no credentials. OpenAI model support remains in the host.

Defaults are `gpt-4o-mini-transcribe`, `gpt-4o-mini-tts`, and voice `alloy`. Users can override model and voice selections.
