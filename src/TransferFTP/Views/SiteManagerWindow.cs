using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace TransferFTP
{
    /// <summary>Add, edit, test and connect to saved sites.</summary>
    public class SiteManagerWindow : ThemedWindow
    {
        public Action<Site, string> ConnectRequested;

        private readonly ListBox _sites;
        private readonly TextBox _name, _host, _port, _user, _keyFile, _remote, _local, _notes;
        private readonly PasswordBox _password, _passphrase;
        private readonly CheckBox _rememberPwd, _active;
        private readonly ComboBox _protocol, _parallel;
        private readonly TextBlock _pwdHint, _testResult, _remoteLabel;
        private readonly StackPanel _sshOnly, _ftpOnly, _form;
        private readonly Border _ftpWarning;
        private readonly TextBlock _empty;
        private Site _editing;
        private bool _loading;

        private readonly System.Collections.Generic.HashSet<string> _created = new System.Collections.Generic.HashSet<string>();
        private readonly Action<Site> _action;

        /// <param name="startNew">Open with a fresh site ready to fill in.</param>
        /// <param name="actionLabel">When set, the left/right buttons are replaced by one button that saves and runs the action.</param>
        public SiteManagerWindow(Site select = null, bool startNew = false, Protocol newProtocol = Protocol.SFTP,
            string actionLabel = null, string actionTip = null, Action<Site> action = null)
        {
            _action = action;
            Title = "Sites";
            Width = 980;
            Height = 700;
            MinWidth = 760;
            MinHeight = 560;

            _sites = new ListBox { Margin = new Thickness(0, 8, 0, 8) };
            _sites.SelectionChanged += (s, e) => { if (!_loading) Load(_sites.SelectedItem as Site); };
            _sites.MouseDoubleClick += (s, e) => { if (_sites.SelectedItem is Site) SaveAndConnect("left"); };
            _sites.ItemTemplate = UI.Template(
                "<StackPanel>" +
                "<TextBlock Text='{Binding Name}' FontWeight='SemiBold' Foreground='{TEXT}' TextTrimming='CharacterEllipsis'/>" +
                "<TextBlock Text='{Binding Summary}' Foreground='{MUTED}' FontSize='12' TextTrimming='CharacterEllipsis' Margin='0,2,0,0'/>" +
                "</StackPanel>");

            var addBtn = FlatButton.Tool(Icons.Plus, "New", "New site|Add a server or network share", NewSite);
            var dupBtn = FlatButton.Tool(Icons.Copy, null, "Duplicate|Make a copy of the selected site to edit", Duplicate);
            var delBtn = FlatButton.Tool(Icons.Trash, null, "Delete site|Remove the saved site and its password. Files on the server aren't touched", DeleteSite);
            var importBtn = FlatButton.Tool(Icons.Download, "Import", "Import and export|Bring in sites from FileZilla or an export file, or export yours", ShowImportMenu);
            var listTools = UI.H(2, addBtn, dupBtn, delBtn, importBtn);
            var sidebar = new DockPanel { Width = 290, Margin = new Thickness(16, 16, 0, 16) };
            var sideTitle = UI.Text("Sites", 20, Theme.Text, FontWeights.SemiBold, true);
            DockPanel.SetDock(sideTitle, Dock.Top);
            DockPanel.SetDock(listTools, Dock.Bottom);
            sidebar.Children.Add(sideTitle);
            sidebar.Children.Add(listTools);
            sidebar.Children.Add(_sites);

            // Form
            _name = UI.Input();
            _protocol = new ComboBox();
            foreach (var p in Site.All) _protocol.Items.Add(Site.ProtocolName(p));
            _protocol.SelectionChanged += (s, e) => ProtocolChanged();
            _host = UI.Input("", double.NaN, "server.example.com or 192.168.1.10");
            _port = UI.Input("22", 90);
            _user = UI.Input();
            _password = UI.Password();
            _password.PasswordChanged += (s, e) => { if (!_loading && _password.Password.Length > 0) _rememberPwd.IsChecked = true; };
            _rememberPwd = new CheckBox { Content = "Remember password (encrypted for your Windows account)" };
            _pwdHint = UI.Text("", 12, Theme.Muted);
            _keyFile = UI.Input("", double.NaN, "Optional. OpenSSH or PuTTY key");
            var keyBrowse = FlatButton.Secondary("Browse", BrowseKey, "Choose key file|Pick your private key, such as id_ed25519 or a PuTTY .ppk");
            keyBrowse.MinHeight = 34;
            _passphrase = UI.Password();
            _remoteLabel = UI.Text("Start in folder", 12, Theme.Muted, FontWeights.SemiBold);
            _remote = UI.Input("", double.NaN, "Leave blank for the home folder");
            _local = UI.Input("", double.NaN, "Optional. Opens on the other side");
            var localBrowse = FlatButton.Secondary("Browse", BrowseLocal, "Choose local folder|Opens on the other side when you connect to this site");
            localBrowse.MinHeight = 34;
            _active = new CheckBox { Content = "Use active mode (only if passive doesn't work)" };
            _parallel = new ComboBox { Width = 260 };
            _parallel.Items.Add("Same as Settings");
            foreach (var n in Settings.ParallelChoices) _parallel.Items.Add(n.ToString());
            _notes = new TextBox { Height = 70, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(8, 6, 8, 6) };

            var hostRow = Row2(UI.Field("Host", _host), UI.Field("Port", _port), 100);
            var keyRow = Row2(UI.Field("Private key file", _keyFile), WithLabelSpace(keyBrowse), 90);
            var localRow = Row2(UI.Field("Local folder", _local), WithLabelSpace(localBrowse), 90);
            _sshOnly = UI.V(14, keyRow, UI.Field("Key passphrase", _passphrase));
            _ftpOnly = UI.V(14, _active);
            _testResult = UI.Text("", 13, Theme.Muted);
            _ftpWarning = Dialogs.WarningNote(Passwords.PlainFtpWarning);

            var save = FlatButton.Primary("Save", () => Save(true), "Save|Keep the changes to this site");
            var test = FlatButton.Secondary("Test connection", Test, "Test connection|Try signing in with these details without saving");
            var left = FlatButton.Secondary("Open on left", () => SaveAndConnect("left"), "Open on left|Save, then connect to this site in the left side");
            var right = FlatButton.Secondary("Open on right", () => SaveAndConnect("right"), "Open on right|Save, then connect to this site in the right side");
            var buttons = UI.H(10, test, left, right, save);
            if (action != null)
            {
                var go = FlatButton.Primary(actionLabel ?? "Connect", SaveAndRun, actionTip);
                save = FlatButton.Secondary("Save", () => Save(true), "Save|Keep the changes to this site");
                buttons = UI.H(10, test, save, go);
            }

            _form = UI.V(14,
                UI.Field("Name", _name),
                UI.Field("Type", _protocol),
                _ftpWarning,
                hostRow,
                UI.Field("Username", _user),
                UI.V(6, UI.Text("Password", 12, Theme.Muted, FontWeights.SemiBold), _password, _pwdHint, _rememberPwd),
                _sshOnly,
                _ftpOnly,
                UI.V(6, _remoteLabel, _remote),
                localRow,
                UI.Field("Files at the same time", _parallel),
                UI.Text("Your own machines on your network or Tailscale handle 6 to 16 easily. Shared web hosts often only allow 2 to 4.", 12, Theme.Muted),
                UI.Field("Notes", _notes),
                _testResult,
                buttons);
            _form.Margin = new Thickness(28, 20, 28, 28);
            var scroll = new ScrollViewer { Content = _form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            _empty = UI.Text("Add a site to get started. Sites hold the address and sign-in for a server or network share.", 14, Theme.Muted);
            _empty.Margin = new Thickness(28);
            _empty.VerticalAlignment = VerticalAlignment.Center;
            _empty.TextAlignment = TextAlignment.Center;

            var formHost = new Grid();
            formHost.Children.Add(scroll);
            formHost.Children.Add(_empty);
            var card = new Border { Background = Theme.Panel, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Margin = new Thickness(16), Child = formHost };

            var root = new DockPanel();
            DockPanel.SetDock(sidebar, Dock.Left);
            root.Children.Add(sidebar);
            root.Children.Add(card);
            Content = root;

            Reload(select?.Id ?? SiteStore.All.OrderByDescending(s => s.LastUsed).FirstOrDefault()?.Id);
            if (startNew) Loaded += (s, e) => NewSite(newProtocol);

            // Sites added here but never filled in are tidied away when the window closes.
            Closed += (s, e) =>
            {
                bool removed = false;
                foreach (var id in _created)
                {
                    var site = SiteStore.Find(id);
                    if (site != null && string.IsNullOrWhiteSpace(site.Host)) { SiteStore.All.Remove(site); removed = true; }
                }
                if (removed) SiteStore.Save();
            };
        }

        private void SaveAndRun()
        {
            if (_editing == null) return;
            if (!Save(true)) return;
            var site = SiteStore.Find(_editing.Id);
            Close();
            _action?.Invoke(site);
        }

        private static Grid Row2(UIElement a, UIElement b, double bWidth)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(bWidth) });
            Grid.SetColumn(b, 2);
            g.Children.Add(a);
            g.Children.Add(b);
            return g;
        }

        private static UIElement WithLabelSpace(FrameworkElement e)
        {
            e.VerticalAlignment = VerticalAlignment.Bottom;
            return e;
        }

        private void Reload(string selectId)
        {
            _loading = true;
            _sites.ItemsSource = null;
            _sites.ItemsSource = SiteStore.All.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            _loading = false;
            var pick = SiteStore.Find(selectId) ?? SiteStore.All.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            _sites.SelectedItem = pick;
            Load(pick);
        }

        private void Load(Site site)
        {
            _editing = site?.Clone();
            bool has = _editing != null;
            _form.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            _empty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            if (!has) return;
            _loading = true;
            _name.Text = site.Name;
            _protocol.SelectedIndex = Array.IndexOf(Site.All, site.Protocol);
            _host.Text = site.Host;
            _port.Text = site.Port.ToString();
            _user.Text = site.User;
            _password.Password = "";
            bool saved = !string.IsNullOrEmpty(site.PasswordProtected);
            _rememberPwd.IsChecked = saved || string.IsNullOrEmpty(site.Host);   // new sites remember by default
            _pwdHint.Text = saved ? "A password is saved. Leave this blank to keep it." : "Leave blank to be asked when you connect.";
            _keyFile.Text = site.KeyFile;
            _passphrase.Password = "";
            _remote.Text = site.RemoteFolder;
            _local.Text = site.LocalFolder;
            _active.IsChecked = site.ActiveMode;
            _parallel.SelectedItem = site.ParallelTransfers > 0 ? site.ParallelTransfers.ToString() : null;
            if (_parallel.SelectedItem == null) _parallel.SelectedIndex = 0;
            _notes.Text = site.Notes;
            _testResult.Text = "";
            _loading = false;
            ProtocolChanged();
        }

        private Protocol SelectedProtocol => _protocol.SelectedIndex >= 0 ? Site.All[_protocol.SelectedIndex] : Protocol.SFTP;

        private void ProtocolChanged()
        {
            var p = SelectedProtocol;
            bool ssh = p == Protocol.SFTP || p == Protocol.SCP;
            bool ftp = p == Protocol.FTP || p == Protocol.FTPS || p == Protocol.FTPSImplicit;
            _sshOnly.Visibility = ssh ? Visibility.Visible : Visibility.Collapsed;
            _ftpOnly.Visibility = ftp ? Visibility.Visible : Visibility.Collapsed;
            _ftpWarning.Visibility = p == Protocol.FTP ? Visibility.Visible : Visibility.Collapsed;
            _remoteLabel.Text = p == Protocol.SMB ? "Share and folder (e.g. media or media/films)" : "Start in folder";
            if (!_loading && int.TryParse(_port.Text, out int port) && Site.All.Any(x => Site.DefaultPort(x) == port))
                _port.Text = Site.DefaultPort(p).ToString();
        }

        /// <summary>Copies the form into the site being edited. Returns false if something is missing.</summary>
        private bool ReadForm(out string problem)
        {
            problem = null;
            if (_editing == null) { problem = "No site selected."; return false; }
            if (string.IsNullOrWhiteSpace(_host.Text)) { problem = "Enter the host name or IP address."; return false; }
            if (!int.TryParse(_port.Text.Trim(), out int port) || port < 1 || port > 65535) { problem = "The port must be a number from 1 to 65535."; return false; }
            _editing.Name = string.IsNullOrWhiteSpace(_name.Text) ? _host.Text.Trim() : _name.Text.Trim();
            _editing.Protocol = SelectedProtocol;
            _editing.Host = _host.Text.Trim().Replace("sftp://", "").Replace("ftp://", "").Replace("ftps://", "").TrimEnd('/');
            _editing.Port = port;
            _editing.User = _user.Text.Trim();
            if (_rememberPwd.IsChecked != true) _editing.PasswordProtected = "";
            else if (_password.Password.Length > 0) _editing.Password = _password.Password;
            if (_password.Password.Length > 0) _editing.SessionPassword = _password.Password;
            _editing.KeyFile = _keyFile.Text.Trim().Trim('"');
            if (_passphrase.Password.Length > 0) _editing.KeyPassphrase = _passphrase.Password;
            _editing.RemoteFolder = _remote.Text.Trim();
            _editing.LocalFolder = _local.Text.Trim().Trim('"');
            _editing.ActiveMode = _active.IsChecked == true;
            _editing.ParallelTransfers = _parallel.SelectedIndex <= 0 ? 0 : int.Parse((string)_parallel.SelectedItem);
            _editing.Notes = _notes.Text;
            return true;
        }

        private bool Save(bool showErrors)
        {
            if (!ReadForm(out string problem))
            {
                if (showErrors) Dialogs.Info("Can't save yet", problem);
                return false;
            }
            if (!PlainFtpOk()) return false;
            SiteStore.Upsert(_editing);
            string id = _editing.Id;
            Reload(id);
            return true;
        }

        /// <summary>
        /// Saving a password for plain FTP is allowed, but the person is told it travels unencrypted first.
        /// Returns false if they cancel.
        /// </summary>
        private bool PlainFtpOk()
        {
            if (_editing.Protocol != Protocol.FTP || string.IsNullOrEmpty(_editing.PasswordProtected) || !Settings.Current.WarnPlainFtp) return true;
            var before = SiteStore.Find(_editing.Id);
            bool newlyRisky = before == null || before.Protocol != Protocol.FTP || string.IsNullOrEmpty(before.PasswordProtected) || _password.Password.Length > 0;
            if (!newlyRisky) return true;
            int pick = Dialogs.Choose("Save the password for plain FTP?", Passwords.PlainFtpWarning + "\n\nYou can still save it and carry on.",
                new[] { "Save anyway", "Switch to FTPS", "Cancel" }, out bool dontAsk, "Don't warn me about this again");
            if (pick == 1)
            {
                _editing.Protocol = Protocol.FTPS;
                _loading = true;
                _protocol.SelectedIndex = Array.IndexOf(Site.All, Protocol.FTPS);
                _loading = false;
                ProtocolChanged();
                return true;
            }
            if (pick != 0) return false;
            if (dontAsk)
            {
                Settings.Current.WarnPlainFtp = false;
                Settings.Current.Save();
            }
            return true;
        }

        private void SaveAndConnect(string side)
        {
            if (_editing == null) return;
            if (!Save(true)) return;
            var site = SiteStore.Find(_editing.Id);
            ConnectRequested?.Invoke(site, side);
            Close();
        }

        private void NewSite() => NewSite(Protocol.SFTP);

        private void NewSite(Protocol protocol)
        {
            var s = new Site { Name = "New site", Protocol = protocol, Port = Site.DefaultPort(protocol) };
            _created.Add(s.Id);
            SiteStore.Upsert(s);
            Reload(s.Id);
            _name.Focus();
            _name.SelectAll();
        }

        private void Duplicate()
        {
            if (!(_sites.SelectedItem is Site src)) return;
            var s = src.Clone();
            s.Id = Guid.NewGuid().ToString("N");
            s.Name = src.Name + " (copy)";
            SiteStore.Upsert(s);
            Reload(s.Id);
        }

        private void DeleteSite()
        {
            if (!(_sites.SelectedItem is Site s)) return;
            if (!Dialogs.Confirm("Delete " + s.Name + "?", "The saved address and password will be removed. Files on the server aren't touched.", "Delete", danger: true)) return;
            SiteStore.Remove(s);
            Reload(null);
        }

        private void ShowImportMenu()
        {
            var m = new ContextMenu();
            var fz = new MenuItem { Header = "From FileZilla" };
            fz.Click += (s, e) => ImportFileZilla();
            var json = new MenuItem { Header = "From a TransferFTP export..." };
            json.Click += (s, e) => ImportJson();
            var export = new MenuItem { Header = "Export sites (without passwords)..." };
            export.Click += (s, e) => ExportJson();
            m.Items.Add(fz);
            m.Items.Add(json);
            m.Items.Add(new Separator());
            m.Items.Add(export);
            m.IsOpen = true;
        }

        private void ImportFileZilla()
        {
            string path = SiteStore.FileZillaPath;
            if (!File.Exists(path))
            {
                var dlg = new OpenFileDialog { Title = "Find FileZilla's sitemanager.xml", Filter = "FileZilla sites|sitemanager.xml|XML files|*.xml" };
                if (dlg.ShowDialog(this) != true) return;
                path = dlg.FileName;
            }
            try
            {
                int n = SiteStore.ImportFileZilla(path);
                Reload(null);
                Dialogs.Info("Imported", n == 0 ? "No sites found in that file." : "Added " + n + " site" + (n == 1 ? "" : "s") + " from FileZilla.");
            }
            catch (Exception ex) { Dialogs.Info("Couldn't import", ex.Message); }
        }

        private void ImportJson()
        {
            var dlg = new OpenFileDialog { Title = "Import sites", Filter = "TransferFTP sites|*.json" };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                int n = SiteStore.Import(dlg.FileName);
                Reload(null);
                Dialogs.Info("Imported", "Added " + n + " site" + (n == 1 ? "" : "s") + ". Passwords aren't included in exports, so you'll be asked when you connect.");
            }
            catch (Exception ex) { Dialogs.Info("Couldn't import", ex.Message); }
        }

        private void ExportJson()
        {
            var dlg = new SaveFileDialog { Title = "Export sites", Filter = "TransferFTP sites|*.json", FileName = "transferftp-sites.json" };
            if (dlg.ShowDialog(this) != true) return;
            try { SiteStore.Export(dlg.FileName); }
            catch (Exception ex) { Dialogs.Info("Couldn't export", ex.Message); }
        }

        private void BrowseKey()
        {
            string ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
            var dlg = new OpenFileDialog { Title = "Choose a private key", Filter = "All files|*.*", InitialDirectory = Directory.Exists(ssh) ? ssh : "" };
            if (dlg.ShowDialog(this) == true) _keyFile.Text = dlg.FileName;
        }

        private void BrowseLocal()
        {
            var dlg = new OpenFolderDialog { Title = "Choose the local folder" };
            if (dlg.ShowDialog(this) == true) _local.Text = dlg.FolderName;
        }

        private void Test()
        {
            if (!ReadForm(out string problem)) { _testResult.Text = problem; _testResult.Foreground = Theme.Red; return; }
            var site = _editing.Clone();
            _testResult.Text = "Connecting to " + site.Host + "...";
            _testResult.Foreground = Theme.Muted;
            Task.Run(() =>
            {
                using var fs = FileSystem.For(site);
                fs.Connect();
                var items = fs.List(fs.Home);
                return fs.Home + " (" + items.Count(i => !i.IsParent) + " items)";
            }).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _testResult.Text = "Couldn't connect: " + TransferQueue.Friendly(t.Exception.GetBaseException());
                    _testResult.Foreground = Theme.Red;
                }
                else
                {
                    _testResult.Text = "Connected. Opened " + t.Result;
                    _testResult.Foreground = Theme.Green;
                    if (_editing != null) _editing.SessionPassword = site.SessionPassword;
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
