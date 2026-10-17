# Docker image

Manage the shared sandbox image on the server running Oscova.

- **Edit Dockerfile** opens the current instructions. Saving a custom file requires a `FROM` instruction.
- **Build image** prepares the image. **Rebuild image** appears when it exists.
- **No cache** rebuilds layers instead of reusing them.
- **Cancel** stops the current build.
- **Use default** restores the bundled Dockerfile and rebuilds after confirmation.
- Closing this page keeps the build running. Reopening shows its status and subsequent output.
- Output keeps up to 500 lines. **Clear** clears this viewer's output.

> **Note:** Only the current operator may edit, build, cancel, or reset the image. Manual build/reset actions are disabled in Offline mode. Builds may download packages and have a five-minute deadline.

> **Note:** The image is shared on this device. Node images and custom Dockerfiles remain local to their nodes. Install Docker on each execution device that needs this environment.

## Related

- [Edit Dockerfile](dockerfile.md): editing and validation.
