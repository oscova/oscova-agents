# Oscova.Agents.Plugins.Web

First-party **web toolset** plugin for Oscova Agents. Gives agents the ability to search the web and fetch news, gated behind user-supplied API keys.

## Tools

- **web__search** — searches the web (via LangSearch) for current information; returns titles, URLs, snippets, and summaries. Requires the **Web Search** credential (`LANGSEARCH_API_KEY`).
- **news__search** — searches recent news articles (via GNews); returns title, source, published time, summary, and URL. Requires the **News** credential (`GNEWS_API_KEY`).

Both tools make outbound network calls (egress) and stay disabled until their credential is configured in **Settings → Credentials**.

## How it's used

This package is not consumed via `<PackageReference>`. The Oscova Agents host installs it from NuGet through its plugin manager and loads it after an administrator enables it. See the documentation for the plugin lifecycle.

## Links

- Website: https://oscova.ai
- Documentation: https://oscova.ai/docs

## License

Apache-2.0
