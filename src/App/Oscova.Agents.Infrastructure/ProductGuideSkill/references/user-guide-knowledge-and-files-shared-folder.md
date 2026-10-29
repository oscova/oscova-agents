# Shared folder

The shared folder lets users on one Oscova Agents server work on the same files. Everyone with access can create, edit and delete its files, and their agents can use it too. Encrypted `.enc` files in it open for every member without another password.

> **Note:**
> Your own files stay private. Only what you save in the shared folder is visible to other users.

## Who can use it

- The operator always has the shared folder.
- Each regular user needs the **Shared folder** permission. New users start with it, and existing users receive it when the server is upgraded.
- The operator turns the permission on or off in the user's edit dialog under **Execution permissions**, then selects **Save changes**. See [Users, roles and PIN](references/user-guide-settings-and-admin-users-roles-pin.md).

Removing the permission takes effect at once. Shared file requests are refused, their agents lose access, and running agent work that uses the folder is stopped. An open Files screen stops offering the folder after it reloads. Their personal files are not affected.

## Use it on the Files screen

1. Open **Settings > Files**.
2. Select **Shared** above the file list. **Home** returns to your own files.
3. Create folders and files, upload, edit, download and delete as you would in **Home**.

- Every change is visible to every member at once. There is no recycle bin.
- Shared files have no file **History**, and the shared folder cannot hold Library documents.
- Uploads follow the same limits as **Home**: up to 10 files of 10 MB or less each.

## Encrypted files

Files ending in `.enc`, such as `plan.md.enc`, are encrypted in the shared folder with the operator's vault key. Every member opens them as readable text in Files, in chat and through agents, without knowing the operator's password.

- Creating or saving an `.enc` file in **Shared** encrypts it automatically.
- Uploading one of your own encrypted files into **Shared** re-encrypts it for the members. If Oscova cannot open the uploaded content with your key, the upload fails and the existing file is left unchanged.
- A downloaded shared `.enc` file is ciphertext. Regular users cannot upload it again. Open it in Files and save its text instead.
- Encrypted files copied into the shared folder outside Oscova, for example by a shell command, do not open. Encrypted shared files copied elsewhere do not open there either. Upload a file instead.
- Encrypted shared files can be opened only while the operator's vault is unlocked, which members already need to use Oscova.

## Let agents use the shared folder

Agents reach the shared folder at `/shared`. Shell commands on the server can use the `ROOT_SHARED` variable.

- Your main agent has access by default.
- For another agent, open it in the agent editor, select **Shared folder** under **Workspace Folders**, then select **Save**.
- Agents get access only while you hold the permission. The option is hidden in the agent editor without it.
- Sub-agents a granted agent calls inherit its access. Scheduled task runs do not use the shared folder.

In chat, open **Files** in the workspace panel and choose **shared** from the folder list to browse and edit the files there. Ask your agent to save a file in the shared folder when other users should have it.

## Empty the shared folder in an emergency

The operator's [duress credentials](references/user-guide-settings-and-admin-profile.md) can include **Empty the shared folder for every user**. Signing in with them deletes the shared folder's contents for everyone immediately. On Windows, files another program holds open are skipped. Regular users cannot add the shared folder to their duress selection.

## Troubleshooting

- **No Shared option in Files:** ask the operator to turn on your **Shared folder** permission.
- **An agent cannot find `/shared`:** confirm **Shared folder** is selected in that agent's editor and saved. Scheduled tasks never get access.
- **An encrypted shared file will not open:** ask the operator to sign in and unlock their vault.
- **An `.enc` upload is refused:** upload only files encrypted with your own key, or save the text through the Files editor.
- **An encrypted shared file says it was encrypted with a different key:** it was copied in from outside Oscova. Ask the person who has the original to upload it.

## Related articles

- [Files](references/user-guide-knowledge-and-files-files.md)
- [Workspaces](references/user-guide-concepts-workspaces.md)
- [Users, roles and PIN](references/user-guide-settings-and-admin-users-roles-pin.md)
- [Profile](references/user-guide-settings-and-admin-profile.md)
