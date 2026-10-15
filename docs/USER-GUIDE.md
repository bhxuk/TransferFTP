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

For a **network share**, put the computer's name or address in **Host** (a Tailscale name like `nas.tailnet.ts.net` works). Leave **Share and folder** blank to see every share on that computer and pick one, or type a share name (like `Media` or `Media/Films`) to open straight into it. Leave the username blank to use your Windows sign-in, or enter an account on that computer (for another Windows PC, `COMPUTERNAME\username` is the safest form).

If a Windows computer over Tailscale can't be reached, its firewall is usually blocking file sharing because Windows treats the Tailscale network as Public. On that computer, allow **File and Printer Sharing** for Public networks, or set the Tailscale network to Private.

**Keys:** choose your private key file in the site. OpenSSH keys and PuTTY `.ppk` keys both work. If the key has a passphrase you can save it, or leave it blank to be asked.

**Passwords:** tick **Remember password** to save it, encrypted for your Windows account (it's ticked for new sites). Leave it unticked and you'll be asked once per session. When you're asked while connecting, tick **Remember this password** to save it then.

**Plain FTP:** you can save the username and password for FTP sites too. Plain FTP isn't encrypted, so the password travels over the network as readable text. TransferFTP shows a warning when you save one, with **Save anyway** to carry on or **Switch to FTPS** if your server supports it. Turn the warning off with **Don't warn me about this again**, or in Settings.

## Space and folder sizes

The bar at the bottom of each side shows:

- **On the left:** how many folders and files are in the folder you're looking at, and how much the files add up to. Select files to see their combined size instead.
- **Include subfolders:** click it to add up everything inside the subfolders as well. Each folder's size appears in the Size column as it's counted, and the full total shows at the bottom. Select some folders first to add up only those. Click **Stop** to cancel. Big folders on a slow server can take a while; you can keep browsing while it counts.
- **On the right:** the free space left on that drive, share or server, with a bar showing how full it is. It turns amber at 75% full and red at 90%. Hover over it for the exact numbers.

Free space works on This PC, network shares and SFTP/SCP servers. FTP has no standard way to report it, so many FTP servers show **Free space not reported**.

## Tabs and windows

Each side can hold several tabs, so you can keep a few servers or folders open at once.

- **Ctrl+T** or the **+** after the tabs opens a new tab. You choose what it opens: **This PC**, **Same as this tab**, any saved site, a quick link, or **New connection...** to add a site and open it straight away. Type to search the list.
- Right-click a tab and choose **Duplicate tab** to copy it without asking.
- **Ctrl+W**, the **x** on a tab, or a middle-click closes it.
- **Ctrl+Tab** and **Ctrl+Shift+Tab** move between tabs on the side you're using.
- Right-click a folder and choose **Open in new tab**.
- Dragging files over a tab switches to it, so you can drop them into a folder open in another tab.
- Opening a different site while one is already connected puts it in a new tab instead of closing the first.
- Your tabs come back next time you start TransferFTP.

**Ctrl+N** or the window button at the top opens another TransferFTP window. All windows share the same transfer queue, and TransferFTP keeps running until you close the last one.

## Quick links and recent folders

- Press **Ctrl+D**, or the **star** button next to the path, to save the folder you're in as a quick link. Right-click a folder and choose **Add folder to quick links** to save it without opening it.
- Quick links for the place you're looking at show as buttons under the path. Click one to jump there, middle-click to open it in a new tab, right-click to rename or remove it.
- The **star** menu lists every quick link, on your PC and on any site. Picking one on another site connects to it.
- The same menu lists the **recent folders** you've opened on that site or on this PC.

## Moving files

- **Drag** files from one side to the other. Hold **Shift** while dropping to move instead of copy.
- **Drop onto a folder** in the list to put the files inside it.
- **Drag from Explorer** onto either side to upload.
- **F5** copies the selection to the folder open on the other side. **F6** moves it.
- Both sides can be servers: drag from one server to another and the files stream through your PC.

### The queue

How many files copy at once is set per site in **Sites > Files at the same time**, or for everything in Settings (4 by default). Your own machines on your home network or Tailscale cope with 6 to 16, which is much faster for lots of small files. Shared web hosts often only allow 2 to 4 connections, so keep those low.

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

Press **Terminal** at the top (or **F9**) and choose which server to open it on: any saved SFTP or SCP site, or **New SSH connection...** to add one. The server you're looking at is already selected, so pressing Enter opens it. The small terminal button inside a connected side opens straight onto that server.

The terminal opens in the folder you're looking at, in full colour, and works with full-screen programs like nano, vim, htop and less.

- **Ctrl+C** stops the running command. With text selected it copies instead.
- **Select** text by dragging; double-click selects a word. **Right-click** copies the selection, or pastes when nothing is selected. **Ctrl+Shift+C** copies, **Ctrl+V** or **Shift+Insert** pastes.
- **Mouse wheel** or **Shift+Page Up / Page Down** scrolls back through earlier output. Typing jumps back to the bottom.
- **Ctrl +** and **Ctrl -** change the text size, **Ctrl+0** resets it.
- **Windows Terminal** opens the same server in Windows Terminal if you prefer it.

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
| Files at the same time | How many transfers run at once (4 by default, up to 16). Each site can set its own number in Sites |
| Retries | How many times to reconnect and resume after a drop |
| Keep the original modified dates | Copies keep the source file's date |
| Show hidden files | Dot files on servers, hidden files on Windows |
| Ask before deleting | The confirmation before a delete |
| Warn before saving a password for plain FTP | The unencrypted FTP warning |
| Edit remote files with | Your editor, or the Windows default |
| Date tolerance | How far apart two dates can be and still count as the same |
| Theme and accent | Dark, light or follow Windows, plus six colours |

## Updates

When a new version is out, an **Update to x.x.x** button appears at the top of the window. Click it once: TransferFTP downloads the new version, installs it and reopens with your tabs as they were. If transfers are running it asks first.

In **Settings > Updates**:

- **Check and update** checks GitHub and, if there's a new version, installs it in the same click.
- **Check for updates automatically** looks when TransferFTP starts and every few hours (on by default).
- **Install updates automatically** updates without asking, but only while nothing is transferring or being edited.

## Hover help

Hold the mouse over any button to see what it does and its keyboard shortcut.

## Troubleshooting

**"Windows protected your PC" when installing.** The installer isn't code-signed. Click **More info**, then **Run anyway**.

**FTP listing hangs or fails.** Try ticking **Use active mode** in the site, or check the server allows passive ports through its firewall.

**"Server key has changed".** Expected after reinstalling a server. If you didn't change anything, don't connect and check with whoever runs the server.

**Certificate problem on FTPS.** Home servers often use self-signed certificates. Check the details and choose **Trust this certificate** if it's yours.

**Logs.** Settings > **Open settings folder** opens `%AppData%\TransferFTP`, where `transferftp.log` records connections and errors.
