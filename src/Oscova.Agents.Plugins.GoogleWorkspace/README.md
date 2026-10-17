# Oscova Google Workspace

Provides Gmail search/read, Google Calendar read, and YouTube subscription/video tools.
The host owns consent, encrypted token storage and refresh. Tools request a usable Google
access token through `AgentPluginContext.ResolveAccessTokenAsync`.

The plugin declares provider titles, service descriptions and Client ID/Client Secret fields.
Hosts render these declarations using their own UI. Account connection does not grant tools.

Oscova installs this plugin by default. Configure the Google app through the operator OAuth
Providers dialog, then connect each user's account in Connected Accounts. Existing custom
Google tool selections need reselection because plugin-qualified tool names replace built-in names.

No Google Drive, Docs or other Workspace services are included.
