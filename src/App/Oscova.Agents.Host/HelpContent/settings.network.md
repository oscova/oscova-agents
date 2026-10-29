# Network

Use Network settings to control host access, trusted private web fetches, and the address Oscova advertises.

> **In plain terms:** choose where Oscova listens and which address generated links should use.

## Change network behavior

- **Allow access from other devices** enables LAN listener intent.
- Set **Port** from 1 to 65535.
- **Allow fetching local and private-network URLs** permits trusted loopback and private pages.
- Reserved, link-local, multicast, and other non-routable fetch targets stay blocked.
- **Allow sandbox network access** lets ordinary online Docker and Microsoft MXC shell runs use the network. It is off by default and applies to every user.
- Click **Save Network Settings** to apply the draft.
- If changing sandbox access affects running work, confirm that it can stop before saving.
- Click **Reset** to discard unsaved edits.

> **Note:** access and port changes require restart. Public URL and private fetch changes apply after saving.

> **Caution:** local-only and air-gapped runs always disable sandbox networking. Users cannot override this Admin setting, and allowed container traffic is outside Oscova's egress ledger.

## Choose and share an address

- **Auto-detected network URL** uses an available LAN address and the active listening port when network access is enabled.
- **Custom URL** requires a complete absolute address.
- For optional HTTPS, configure Tailscale Serve on the host and enter its address as **Custom URL**.
- **Effective Public App URL** is used for QR sharing and generated absolute links.
- **Active Server URLs** shows listeners opened during startup.
- Expand **QR Code** to scan or download the effective URL.

> **Note:** custom URLs do not configure listeners, proxies, or OAuth callbacks. OAuth uses the current browser-facing app address.

> **Note:** Tailscale Serve can forward to the localhost listener with LAN access disabled. Normal sign-in and Vault unlock apply.

## Related

- [Privacy](settings.privacy.md) - control public agent egress.
- [Connected Accounts](settings.connected-accounts.md) - connect OAuth providers.
