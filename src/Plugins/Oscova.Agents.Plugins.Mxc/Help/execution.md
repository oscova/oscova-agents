# Microsoft MXC

Shell commands and skill scripts use the machine's native isolation: ProcessContainer on Windows, Bubblewrap on Linux, or Seatbelt on macOS. Selection automatically checks prerequisites before Save becomes available.

Existing workspace grants determine which folders commands can change. The sandbox network permission controls outbound connections, and offline runs deny them. Inbound connections and host loopback are denied. Operating-system files and installed tool directories may remain readable.

Variables and activated environments stay in the foreground shell between commands for the same chat and agent. Each command starts in the granted working directory. Cleanup or recovery creates a fresh shell. Changes made to writable workspace files are real changes.

MXC needs its native dependencies on every executing machine. See the [Microsoft MXC project](https://github.com/microsoft/mxc) for operating-system prerequisites. A failed check does not fall back to Host.
