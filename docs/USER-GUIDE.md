# TransferFTP user guide

## Getting started

1. Open TransferFTP. The left side shows **This PC**, your home folder.
2. Click **Sites** at the top, then **New**.
3. Pick the type, type the host, username and password, and press **Test connection**.
4. Press **Open on right**. The server opens on the right, your PC stays on the left.

The first time you connect to an SSH server you'll see its fingerprint. Check it matches the server, then choose **Trust and connect**. TransferFTP remembers it and warns you if it ever changes.

## Site types

| Type | Use it for | Default port |
|---|---|---|
| SFTP | Linux servers, NAS boxes, anything with SSH | 22 |
| SCP | Older SSH servers that only allow SCP copies | 22 |
| FTP | Plain FTP. Not encrypted, so avoid it over the internet | 21 |
| FTPS (explicit) | FTP with TLS, the usual kind for web hosts | 21 |
| FTPS (implicit) | Older FTPS servers | 990 |
| Network share | Windows shares and NAS shares (`\\server\share`) | 445 |

For a **network share**, put the server in Host and the share in **Share and folder**, for example `media` or `media/films`. Leave the username blank to use your Windows sign-in.

**Keys:** choose your private key file in the site. OpenSSH keys and PuTTY `.ppk` keys both work. If the key has a passphrase you can save it, or leave it blank to be asked.

**Passwords:** tick **Remember password** to save it, encrypted for your Windows account. Leave it unticked and you'll be asked once per session.

## Moving files

- **Drag** files from one side to the other. Hold **Shift** while dropping to move instead of copy.
- **Drop onto a folder** in the list to put the files inside it.
- **Drag from Explorer** onto either side to upload.
- **F5** copies the selection to the folder open on the other side. **F6** moves it.
- Both sides can be servers: drag from one server to another and the files stream through your PC.

### The queue

Everything you copy goes into the queue at the bottom.

- **Pause all** stops every transfer where it is. **Resume all** carries on from the same byte.
- Right-click a transfer to pause, resume, retry or cancel just that one.
- If a connection drops, TransferFTP waits a moment, reconnects and resumes. Change the number of retries in Settings.
- **Clear finished** tidies the list.

### When a file already exists

By default you're asked each time:

- **Overwrite** replaces it.
- **Skip** leaves it.
- **Resume** carries on a half-finished copy (only when the existing file is smaller).
- **Overwrite if newer** only copies when your file is newer.
- **Keep both** saves the new one as `name (2).ext`.

Tick **Do this for the rest of these files** to answer once for a whole folder. Set a permanent answer in Settings.

## Compare and sync

Open the two folders you want to compare, one on each side, then press **Sync**.

1. Choose a direction: left to right, right to left, or both ways (the newer file wins).
2. Choose **Size and date** or **Size only**. Size only is handy when dates don't survive copying.
3. Add anything to skip, separated by `;`. Wildcards like `*.log` work.
4. Press **Compare**. You'll see every file that would be copied, created or deleted.
5. Untick anything you want left alone, then press **Sync**. The changes go into the queue.

Deleting is off unless you tick **Delete files that only exist on the target**, and you're asked to confirm.

A **conflict** means the same file changed on both sides and it isn't clear which is newer. Conflicts are never copied automatically.

Some FTP servers only report dates to the minute. If every file looks different, set the date tolerance in Settings to 60 seconds.

## Editing files on a server

Double-click a file on a server (or press **F4**). It downloads, opens in the app Windows uses for that type, and every time you save, it uploads back. The **Editing** tab lists files being watched; press **Stop watching** when you're done.

To always use one editor, such as VS Code or Notepad++, choose it in **Settings > Edit remote files with**.

## Terminal

For SFTP and SCP sites, press **Terminal** (or **F9**). It opens in the folder you're looking at.

- **Ctrl+C** stops the running command. With text selected it copies instead.
- **Right-click** or **Ctrl+V** pastes.
- Full-screen programs like vim, nano and htop work best in **Windows Terminal**: press that button to open the same server there.

## Permissions

Right-click files on an SFTP or FTP server and choose **Permissions**. Tick read, write and run for owner, group and everyone, or type the number (like `644` or `755`).

## Importing sites

In **Sites**, press **Import**:

- **From FileZilla** reads FileZilla's saved sites, including passwords that FileZilla saved without a master password.
- **From a TransferFTP export** loads a file made with **Export sites**. Exports never include passwords.

## Settings

| Setting | What it changes |
|---|---|
| When a file already exists | Ask, or always overwrite, skip, overwrite if newer, or resume |
| Files at the same time | How many transfers run at once (2 is a good start) |
| Retries | How many times to reconnect and resume after a drop |
| Keep the original modified dates | Copies keep the source file's date |
| Show hidden files | Dot files on servers, hidden files on Windows |
| Ask before deleting | The confirmation before a delete |
| Edit remote files with | Your editor, or the Windows default |
| Date tolerance | How far apart two dates can be and still count as the same |
| Theme and accent | Dark, light or follow Windows, plus six colours |

## Troubleshooting

**"Windows protected your PC" when installing.** The installer isn't code-signed. Click **More info**, then **Run anyway**.

**FTP listing hangs or fails.** Try ticking **Use active mode** in the site, or check the server allows passive ports through its firewall.

**"Server key has changed".** Expected after reinstalling a server. If you didn't change anything, don't connect and check with whoever runs the server.

**Certificate problem on FTPS.** Home servers often use self-signed certificates. Check the details and choose **Trust this certificate** if it's yours.

**Logs.** Settings > **Open settings folder** opens `%AppData%\TransferFTP`, where `transferftp.log` records connections and errors.
