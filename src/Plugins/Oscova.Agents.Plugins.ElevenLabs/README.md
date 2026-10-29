# Oscova ElevenLabs

Optional speech recognition and synthesis for Oscova Agents using a saved ElevenLabs API-key credential.

Install and enable the plugin, restart the harness, then select ElevenLabs in Speech settings. Add its user credential in Credentials using an API key or the suggested `ELEVENLABS_API_KEY` environment reference. Select that credential for each capability and choose an accessible account voice for synthesis. Save speech settings before testing.

Recognition defaults to `scribe_v2` and accepts `scribe_v1`. Synthesis defaults to `eleven_flash_v2_5` and returns 24 kHz mono audio compatible with browser and Avatar playback. Voice discovery retrieves your account's accessible voices without generating speech. Speech generation and transcription consume service credits.

Speed, stability and similarity controls are deferred. The plugin uses the community ElevenLabs .NET SDK and the harness's guarded transport, selected user credentials and encrypted settings.
