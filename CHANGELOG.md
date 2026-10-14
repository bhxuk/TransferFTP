# What's new

## 1.2.0

- **One-click updates.** When a new version is out, an **Update** button appears at the top of the window. One click downloads it, installs it and reopens TransferFTP with your tabs as they were. **Settings > Updates** has a **Check and update** button, and you can choose to install updates automatically.
- **New tabs ask where to open.** The **+** button and Ctrl+T now let you pick This PC, the same place as the current tab, any saved site, a quick link, or a new connection. Search to find a site quickly.
- **Terminal asks which server.** The **Terminal** button at the top (and F9) lets you choose any saved SFTP or SCP server, or add a new one. The terminal button inside a connected side still opens straight onto that server.
- **Hover help on every button.** Every button now explains what it does, with its keyboard shortcut.
- **Neater filter box,** with a search icon, proper spacing and a clear button. Esc clears it.
- Text boxes across the app have more breathing room for their placeholder text.

## 1.1.2

- **4 files at a time by default** instead of 2. If you were still on the old default, you're moved to 4 automatically.
- **Each site can set its own number**, from 1 to 16, in Sites > Files at the same time. Use a high number for your own machines on your network or Tailscale, and keep shared web hosts at 2 to 4.
- The Settings number now goes up to 16 and is used for sites that don't set their own.

## 1.1.1

- **The terminal now shows colours.** Your prompt, `ls`, `git`, `htop` and editors show their colours, including 256-colour and true-colour themes.
- **Fixed: the Space key didn't work in the terminal**, so `df -k` arrived as `df-k`.
- The terminal is now a proper xterm-style screen, so full-screen programs like `nano`, `vim`, `htop`, `less` and `mc` work inside TransferFTP. It also resizes with the window.
- Select text with the mouse (double-click selects a word). Right-click copies a selection, or pastes when nothing is selected. Ctrl+Shift+C and Ctrl+V work too.
- Scroll back through earlier output with the mouse wheel or Shift+Page Up.
- Ctrl + and Ctrl - change the text size.

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
