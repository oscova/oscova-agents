# Network

Network settings control how the Oscova Agents host listens, whether the web fetch tool may read trusted private pages, whether sandbox shells get network access and which address Oscova advertises for QR sharing and absolute links. Only the operator can see or change them. The settings are system-wide, and requests from regular users are rejected.

> **Note:**
> A listener is the address and port where the web server accepts connections. The public app URL is an address Oscova advertises. It does not configure DNS, TLS, a proxy or the listener itself.

## Allow access from other devices

1. Enable **Allow access from other devices** when phones, tablets, other computers or LAN services must open Oscova directly over the LAN.
2. Set **Port** from 1 to 65535. The default is 11317.
3. Select **Save Network Settings**. **Reset** discards unsaved edits.
4. Restart Oscova.



> **Important:**
> Saved access and port changes take effect only after a restart. The warning stays while the saved listener differs from **Active Server URLs**.

After restarting:

- **Local URL** uses `localhost` and the port Oscova is listening on, including an `oscova start --port` override.
- **Auto Network URL** uses a detected LAN IPv4 address and that same port when an address is available.
- **Active Server URLs** shows the listeners opened during startup.

Enabling network access does not change firewall rules. If another device still cannot connect, allow the selected port through the host firewall and any network controls between the devices.

## Allow trusted private web fetches

The built-in `web__fetch` tool accepts public HTTP and HTTPS destinations but rejects loopback and private-network addresses. This stops a malicious page or prompt from making the agent inspect an internal service.

Enable **Allow fetching local and private-network URLs** only when agents must read trusted resources such as a local documentation server, a private intranet page or a service on a private LAN address.

- The setting takes effect after save without a host restart.
- It allows loopback, RFC 1918 private IPv4 and IPv6 unique-local destinations. Unspecified, link-local, carrier-grade NAT, multicast and reserved ranges stay blocked.
- Address checks also apply after DNS resolution and redirects. A redirect to a disallowed address is rejected.

> **Caution:**
> This toggle weakens `web__fetch` protection. It does not grant browser login access and does not override Offline mode, which removes the Internet-scoped `web__fetch` tool from offline agent runs.

## Allow sandbox network access

**Allow sandbox network access** is an operator-controlled, installation-wide grant for sandbox shell execution. It is off by default, and individual users cannot enable or override it for themselves.

- When enabled, ordinary online Docker runs use bridge networking and Microsoft MXC permits outbound connections. MXC still denies inbound and host-loopback access.
- The setting takes effect after **Save Network Settings** without a host restart. Changing it asks for confirmation when affected work must be interrupted.
- Offline and local-only runs always deny sandbox networking, even when the grant is enabled.

> **Caution:**
> A network-enabled container can contact destinations independently of Oscova's HTTP clients. That traffic is outside Oscova's egress ledger, so enable the grant only when all users and sandbox workloads are trusted with network access.

## Optional HTTPS with Tailscale Serve

A reverse proxy accepts HTTPS connections and forwards them to Oscova's local HTTP listener. Tailscale Serve provides this for devices on your private Tailscale network and manages the HTTPS certificate. Oscova needs no certificate purchase, certificate import or browser warning bypass.

1. Install Tailscale on the Oscova computer and the connecting devices, and sign in to your Tailscale network.

2. Keep **Allow access from other devices** disabled and start Oscova with `oscova start`.

3. In another terminal on the same computer, run the following command. Replace `11317` if you changed Oscova's port.
   
   ```text
   tailscale serve --bg http://127.0.0.1:11317
   ```

4. Follow Tailscale's prompt to enable HTTPS if needed, then open the HTTPS address printed by Tailscale from a connected device.

5. Sign in and unlock the Vault normally. To share this address through Oscova's QR code, select **Custom URL**, enter the printed HTTPS address and save.

Oscova and Tailscale must both be running. **Active Server URLs** still shows the local HTTP listener because Tailscale handles HTTPS separately. For Chrome app installation, open the HTTPS address and use Chrome's install button when it appears.

Use Serve for private access. Tailscale Funnel is a separate feature that exposes a service to the public Internet.

### Other local reverse proxies

An equivalent proxy must:

- Run on the Oscova computer and connect from `127.0.0.1` or `::1`.
- Serve Oscova at `/`, preserve the browser-facing `Host` and support WebSockets.
- Replace client-supplied forwarding headers with the actual client address in `X-Forwarded-For` and the external scheme in `X-Forwarded-Proto`.

Oscova trusts those two headers from one exact loopback proxy hop. Forwarded host and path-prefix headers are not used. Remote proxied clients remain remote for local runtime control authorization.

## Choose the public app URL

**Mode** controls the address Oscova advertises:

- **Auto-detected network URL** uses the detected LAN address and the listening port. It is unavailable when network access is disabled or no suitable interface is detected.
- **Custom URL** uses the complete absolute address you enter, such as `https://oscova.company.local`. Use it when a reverse proxy, tunnel, hostname or HTTPS gateway provides the externally reachable address.

Saving a public URL updates later generated links and the Network summary without restarting the listener. Oscova does not verify that the custom address resolves or is reachable.

**Effective Public App URL** shows the selected advertised address. Oscova uses it for the QR code and when a feature must turn an app-relative resource into an absolute URL, such as media consumed by another runtime.

> **Important:**
> OAuth callbacks do not use this public URL setting. Connected Accounts builds its callback from the current request scheme, host and path base. Open Oscova through the address the provider can return to before starting OAuth consent.

## Share the QR code

When an effective public URL exists, expand **QR Code** and scan it from another device. Select **Download QR Code** to save the PNG.

The QR code contains only the effective URL. It does not bypass login, configure the firewall or prove the address is reachable.

## Troubleshooting

- **LAN device cannot connect directly**: enable network access, save, restart Oscova and check the firewall.
- **Tailscale HTTPS address cannot connect**: check that Oscova and Tailscale are running, Serve targets Oscova's current port and both devices are connected to your Tailscale network.
- **Restart warning remains**: compare the saved access and port with **Active Server URLs**.
- **Auto network URL unavailable**: use a custom URL or fix the host's active network interface.
- **Port rejected**: choose a value from 1 to 65535.
- **Custom URL rejected**: enter a complete absolute URL with a scheme.
- **Custom URL opens the wrong service**: correct the proxy, DNS or URL. Saving does not configure them.
- **OAuth returns to the wrong host**: reopen Oscova through the intended address, then restart the connection flow.
- **Private `web__fetch` target blocked**: enable the private fetch setting only for a trusted destination.
- **Target remains blocked**: its resolved address may be link-local, reserved, multicast or otherwise non-routable.
- **QR code is wrong**: check **Mode** and **Effective Public App URL**, then regenerate it.

## Related articles

- [Privacy](references/user-guide-settings-and-admin-privacy.md)
- [MCP servers](references/user-guide-connections-mcp-servers.md)
- [Connected accounts](references/user-guide-connections-connected-accounts.md)
- [Logs](references/user-guide-settings-and-admin-logs.md)
