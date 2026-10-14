# What's new

## 1.1.0

- **Tabs on both sides.** Open several places at once on the left and the right. Ctrl+T opens a new tab at the same place, Ctrl+W closes it, Ctrl+Tab switches. Middle-click a tab to close it, and drag files over a tab to drop into it. Tabs come back next time you open TransferFTP.
- **More windows.** Ctrl+N (or the window button at the top) opens another TransferFTP window. They share the same transfer queue.
- **Quick links.** Press Ctrl+D or the star button to save the folder you're in. Your links for that place show as buttons under the path, and the star menu lists all of them, on any site. Right-click a link to rename it, remove it or open it in a new tab.
- **Recent folders.** The star menu also lists the last folders you opened on that site or on this PC.
- **Saving FTP passwords.** Usernames and passwords save for every site type, including plain FTP. For plain FTP you'll see a warning that it isn't encrypted, then it saves anyway (or switch to FTPS in one click). You can turn the warning off in Settings.
- **Remember from the password prompt.** When you're asked for a password while connecting, tick "Remember this password" to save it.
- New sites remember their password by default.
- Opening a different site while one is connected now opens it in a new tab instead of replacing it.

## 1.0.0

The first release.

- Two-pane browser for This PC, saved sites and network shares, with drag and drop between panes and from Explorer
- SFTP and SCP with passwords, keys (OpenSSH and PuTTY) and keyboard-interactive sign-in
- FTP, FTPS explicit and FTPS implicit, passive or active mode
- Network shares (SMB) with optional sign-in
- Transfer queue: parallel transfers, speed and time left, pause, resume, cancel, automatic retry and resume after a dropped connection
- "File already exists" choices: overwrite, skip, resume, overwrite if newer, keep both, with "do this for the rest"
- Server-to-server copies
- Compare and sync with a full preview, three directions, skip lists and optional deletes
- Edit remote files in any editor with automatic upload on save
- Built-in SSH terminal, plus a shortcut to Windows Terminal
- Permissions editor, filter, natural sorting, back history
- Site manager with test connection, import from FileZilla, export without passwords
- Encrypted passwords, remembered server keys and certificate checks
- Light, dark and system themes with six accent colours
