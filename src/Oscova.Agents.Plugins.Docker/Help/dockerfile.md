# Edit Dockerfile

Change the Docker instructions used for the shared sandbox image.

- A custom Dockerfile must contain a `FROM` instruction.
- **Save** stores the instructions and closes the editor after success.
- **Cancel** closes without saving your edits.
- Saving does not rebuild an existing image. Use **Build image** or **Rebuild image** afterward.
- A failed save leaves the editor open with its error.

> **Note:** These shared instructions are nonsecret installation configuration. Do not put credentials or private user data in them.

## Related

- [Docker image](image.md): build, cancel, and restore the default.
