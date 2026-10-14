using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace TransferFTP
{
    public class SettingsWindow : ThemedWindow
    {
        public SettingsWindow()
        {
            Title = "Settings";
            Width = 620;
            Height = 700;
            MinHeight = 420;
            var s = Settings.Current;

            string[] whenExists = { "Ask", "Overwrite", "Skip", "Newer", "Resume" };
            string[] whenLabels = { "Ask me", "Overwrite", "Skip", "Overwrite if newer", "Resume if smaller" };
            var exists = UI.Combo(whenLabels, whenLabels[Math.Max(0, Array.IndexOf(whenExists, s.WhenFileExists))], 220);
            exists.SelectionChanged += (o, e) => s.WhenFileExists = whenExists[exists.SelectedIndex];

            var parallel = UI.Combo(new[] { "1", "2", "3", "4", "6", "8" }, s.ParallelTransfers.ToString(), 120);
            parallel.SelectionChanged += (o, e) => s.ParallelTransfers = int.Parse((string)parallel.SelectedItem);

            var retries = UI.Combo(new[] { "0", "1", "2", "3", "5" }, s.RetryCount.ToString(), 120);
            retries.SelectionChanged += (o, e) => s.RetryCount = int.Parse((string)retries.SelectedItem);

            var tolerance = UI.Combo(new[] { "0", "2", "60", "3600" }, s.SyncToleranceSeconds.ToString(), 120);
            tolerance.SelectionChanged += (o, e) => s.SyncToleranceSeconds = int.Parse((string)tolerance.SelectedItem);

            CheckBox Tick(string text, bool value, Action<bool> set)
            {
                var c = new CheckBox { Content = text, IsChecked = value };
                c.Click += (o, e) => set(c.IsChecked == true);
                return c;
            }

            var editor = UI.Input(s.EditorPath, double.NaN, "Windows default for each file type");
            editor.TextChanged += (o, e) => s.EditorPath = editor.Text.Trim().Trim('"');
            var browse = FlatButton.Secondary("Browse", () =>
            {
                var dlg = new OpenFileDialog { Title = "Choose an editor", Filter = "Programs|*.exe" };
                if (dlg.ShowDialog(this) == true) editor.Text = dlg.FileName;
            });
            browse.MinHeight = 34;
            var editorRow = new DockPanel();
            DockPanel.SetDock(browse, Dock.Right);
            browse.Margin = new Thickness(10, 0, 0, 0);
            editorRow.Children.Add(browse);
            editorRow.Children.Add(editor);

            var theme = UI.Combo(Theme.ThemeNames, s.Theme, 160);
            theme.SelectionChanged += (o, e) => s.Theme = (string)theme.SelectedItem;
            var accent = UI.Combo(Theme.AccentNames, s.Accent, 160);
            accent.SelectionChanged += (o, e) => s.Accent = (string)accent.SelectedItem;

            var openFolder = FlatButton.Secondary("Open settings folder", () =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Settings.Folder) { UseShellExecute = true }); } catch { }
            });

            var body = UI.V(18,
                UI.Text("Settings", 22, Theme.Text, FontWeights.SemiBold, true),
                Section("Transfers",
                    UI.Field("When a file already exists", exists),
                    UI.Field("Files at the same time", parallel),
                    UI.Field("Retries after a dropped connection", retries),
                    Tick("Keep the original modified dates", s.PreserveTimestamps, v => s.PreserveTimestamps = v)),
                Section("Files",
                    Tick("Show hidden files", s.ShowHiddenFiles, v => s.ShowHiddenFiles = v),
                    Tick("Ask before deleting", s.ConfirmDelete, v => s.ConfirmDelete = v),
                    Tick("Warn before saving a password for plain (unencrypted) FTP", s.WarnPlainFtp, v => s.WarnPlainFtp = v),
                    UI.Field("Edit remote files with", editorRow)),
                Section("Sync",
                    UI.Field("Treat dates this many seconds apart as the same (use 60 for FTP servers that round to the minute)", tolerance)),
                Section("Look",
                    UI.H(14, UI.Field("Theme", theme), UI.Field("Accent", accent)),
                    UI.Text("Theme changes apply next time TransferFTP starts.", 12, Theme.Muted)),
                openFolder);
            openFolder.HorizontalAlignment = HorizontalAlignment.Left;
            body.Margin = new Thickness(24);
            Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Closed += (o, e) => s.Save();
        }

        private static Border Section(string title, params UIElement[] children)
        {
            var v = UI.V(12, UI.Label(title));
            foreach (var c in children) v.Children.Add(c);
            return UI.Card(v, 18, Theme.Panel, 14);
        }
    }
}
