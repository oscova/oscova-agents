# Oscova Microsoft Graph

Provides Outlook mail and Microsoft To Do tools through Microsoft Graph. The host owns consent, encrypted token storage and refresh. Tools request a usable Microsoft access token through `AgentPluginContext.ResolveAccessTokenAsync`.

The plugin declares the Microsoft provider title, services and Client ID/Client Secret fields. Hosts render these declarations using their own UI. Account connection does not grant tools.

Oscova installs this plugin by default. Configure the Microsoft app through the operator OAuth Providers dialog, then connect each user's account in Connected Accounts. Existing custom Microsoft tool selections need reselection because plugin-qualified tool names replace built-in names.

The existing Outlook and To Do permissions are unchanged. Teams, OneDrive, SharePoint and Microsoft Calendar are not included.
