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
        private readonly ComboBox _protocol;
        private readonly TextBlock _pwdHint, _testResult, _remoteLabel;
        private readonly StackPanel _sshOnly, _ftpOnly, _form;
        private readonly TextBlock _empty;
        private Site _editing;
        private bool _loading;

        public SiteManagerWindow(Site select = null)
        {
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

            var addBtn = FlatButton.Tool(Icons.Plus, "New", "Add a site", NewSite);
            var dupBtn = FlatButton.Tool(Icons.Copy, null, "Duplicate", Duplicate);
            var delBtn = FlatButton.Tool(Icons.Trash, null, "Delete site", DeleteSite);
            var importBtn = FlatButton.Tool(Icons.Download, "Import", "Import sites", ShowImportMenu);
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
            var keyBrowse = FlatButton.Secondary("Browse", BrowseKey);
            keyBrowse.MinHeight = 34;
            _passphrase = UI.Password();
            _remoteLabel = UI.Text("Start in folder", 12, Theme.Muted, FontWeights.SemiBold);
            _remote = UI.Input("", double.NaN, "Leave blank for the home folder");
            _local = UI.Input("", double.NaN, "Optional. Opens on the other side");
            var localBrowse = FlatButton.Secondary("Browse", BrowseLocal);
            localBrowse.MinHeight = 34;
            _active = new CheckBox { Content = "Use active mode (only if passive doesn't work)" };
            _notes = new TextBox { Height = 70, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(8, 6, 8, 6) };

            var hostRow = Row2(UI.Field("Host", _host), UI.Field("Port", _port), 100);
            var keyRow = Row2(UI.Field("Private key file", _keyFile), WithLabelSpace(keyBrowse), 90);
            var localRow = Row2(UI.Field("Local folder", _local), WithLabelSpace(localBrowse), 90);
            _sshOnly = UI.V(14, keyRow, UI.Field("Key passphrase", _passphrase));
            _ftpOnly = UI.V(14, _active);
            _testResult = UI.Text("", 13, Theme.Muted);

            var save = FlatButton.Primary("Save", () => Save(true));
            var test = FlatButton.Secondary("Test connection", Test);
            var left = FlatButton.Secondary("Open on left", () => SaveAndConnect("left"));
            var right = FlatButton.Secondary("Open on right", () => SaveAndConnect("right"));
            var buttons = UI.H(10, test, left, right, save);

            _form = UI.V(14,
                UI.Field("Name", _name),
                UI.Field("Type", _protocol),
                hostRow,
                UI.Field("Username", _user),
                UI.V(6, UI.Text("Password", 12, Theme.Muted, FontWeights.SemiBold), _password, _pwdHint, _rememberPwd),
                _sshOnly,
                _ftpOnly,
                UI.V(6, _remoteLabel, _remote),
                localRow,
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
            _rememberPwd.IsChecked = saved;
            _pwdHint.Text = saved ? "A password is saved. Leave this blank to keep it." : "Leave blank to be asked when you connect.";
            _keyFile.Text = site.KeyFile;
            _passphrase.Password = "";
            _remote.Text = site.RemoteFolder;
            _local.Text = site.LocalFolder;
            _active.IsChecked = site.ActiveMode;
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
            SiteStore.Upsert(_editing);
            string id = _editing.Id;
            Reload(id);
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

        private void NewSite()
        {
            var s = new Site { Name = "New site" };
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
