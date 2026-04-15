using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using CloudFrame.Core.Config;
using CloudFrame.Core.Filtering;
using CloudFrame.Providers.OneDrive;

namespace CloudFrame.App
{
    public sealed class SettingsForm : Form
    {
        private readonly AppSettings _settings;
        private readonly SettingsService _settingsService;
        private readonly List<AccountConfig> _accounts;

        // Accounts tab
        private ListBox _accountList = null!;
        private Button _btnAddAccount = null!;
        private Button _btnRemoveAccount = null!;
        private Button _btnSignIn = null!;
        private ComboBox _cmbEdgeProfile = null!;
        private TextBox _txtDisplayName = null!;
        private NumericUpDown _numWeight = null!;
        private ListBox _folderList = null!;
        private Button _btnAddFolder = null!;
        private Button _btnRemoveFolder = null!;

        // Filters tab
        private ListBox _filterList = null!;
        private TextBox _txtFilterName = null!;
        private ComboBox _cmbAction = null!;
        private ComboBox _cmbPatternType = null!;
        private TextBox _txtPattern = null!;
        private CheckBox _chkFilterEnabled = null!;

        // Options tab
        private NumericUpDown _numSlideDuration = null!;
        private NumericUpDown _numPrefetch = null!;
        private NumericUpDown _numCacheSize = null!;
        private ComboBox _cmbTransition = null!;
        private NumericUpDown _numTransitionMs = null!;
        private CheckBox _chkRunOnStartup = null!;

        public SettingsForm(AppSettings settings, SettingsService settingsService)
        {
            _settings = settings;
            _settingsService = settingsService;
            _accounts = settings.Accounts.Select(CloneAccount).ToList();
            BuildUI();
        }

        private void BuildUI()
        {
            Text = "CloudFrame — Settings";
            Size = new Size(700, 560);
            MinimumSize = new Size(640, 520);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;

            // IMPORTANT: Add Dock=Bottom controls BEFORE Dock=Fill controls.
            // WinForms lays out docked controls in reverse z-order.

            // ── OK / Cancel row ────────────────────────────────────────────────
            var btnOk = new Button { Text = "OK", Size = new Size(88, 28) };
            btnOk.Click += OnOkClick;
            var btnCancel = new Button
            {
                Text = "Cancel",
                Size = new Size(88, 28),
                DialogResult = DialogResult.Cancel
            };

            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 44,
                Padding = new Padding(8, 8, 8, 4)
            };
            buttonPanel.Controls.Add(btnCancel);
            buttonPanel.Controls.Add(btnOk);
            Controls.Add(buttonPanel);   // add Bottom FIRST

            AcceptButton = btnOk;
            CancelButton = btnCancel;

            // ── Tab control (fills remaining space) ────────────────────────────
            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(BuildAccountsTab());
            tabs.TabPages.Add(BuildFiltersTab());
            tabs.TabPages.Add(BuildOptionsTab());
            Controls.Add(tabs);          // add Fill SECOND

            PopulateAccountList();
            PopulateOptions();
        }

        // ══════════════════════════════════════════════════════════════════════
        // ACCOUNTS TAB
        // ══════════════════════════════════════════════════════════════════════

        private TabPage BuildAccountsTab()
        {
            var page = new TabPage("Accounts");

            // Use a TableLayoutPanel so the layout is robust regardless of
            // DPI or font scaling.
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(8)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // ── Left column: account list + buttons ────────────────────────────
            var leftPanel = new Panel { Dock = DockStyle.Fill };

            var lblAccounts = new Label
            {
                Text = "Cloud accounts:",
                Location = new Point(0, 0),
                AutoSize = true
            };

            _accountList = new ListBox
            {
                Location = new Point(0, 20),
                Size = new Size(188, 200),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _accountList.SelectedIndexChanged += OnAccountSelected;

            _btnAddAccount = new Button
            {
                Text = "Add OneDrive…",
                Location = new Point(0, 228),
                Size = new Size(120, 28)
            };
            _btnAddAccount.Click += OnAddAccountClick;

            _btnRemoveAccount = new Button
            {
                Text = "Remove",
                Location = new Point(0, 262),
                Size = new Size(120, 28)
            };
            _btnRemoveAccount.Click += OnRemoveAccountClick;

            _btnSignIn = new Button
            {
                Text = "Sign in…",
                Location = new Point(0, 296),
                Size = new Size(120, 28)
            };
            _btnSignIn.Click += OnSignInClick;

            leftPanel.Controls.AddRange(new Control[]
            {
                lblAccounts, _accountList,
                _btnAddAccount, _btnRemoveAccount, _btnSignIn
            });

            // ── Right column: account details ──────────────────────────────────
            var rightPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 0, 0) };

            var lblName = new Label { Text = "Display name:", Location = new Point(8, 0), AutoSize = true };
            _txtDisplayName = new TextBox
            {
                Location = new Point(8, 18),
                Width = 220,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _txtDisplayName.TextChanged += OnDisplayNameChanged;

            var lblEdgeProfile = new Label { Text = "Edge profile for sign-in:", Location = new Point(8, 50), AutoSize = true };
            _cmbEdgeProfile = new ComboBox
            {
                Location = new Point(8, 68),
                Width = 340,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            PopulateEdgeProfiles();
            _cmbEdgeProfile.SelectedIndexChanged += OnEdgeProfileChanged;

            var lblWeight = new Label { Text = "Selection weight:", Location = new Point(8, 100), AutoSize = true };
            _numWeight = new NumericUpDown
            {
                Location = new Point(8, 118),
                Width = 80,
                Minimum = 0.1m,
                Maximum = 10m,
                DecimalPlaces = 1,
                Increment = 0.5m,
                Value = 1m
            };
            _numWeight.ValueChanged += OnWeightChanged;

            var lblWeightHelp = new Label
            {
                Text = "Higher = more likely to be picked when multiple accounts are configured.",
                Location = new Point(8, 142),
                Size = new Size(350, 32),
                ForeColor = SystemColors.GrayText,
                AutoSize = false
            };

            var lblFolders = new Label { Text = "Root folders to scan:", Location = new Point(8, 178), AutoSize = true };
            _folderList = new ListBox
            {
                Location = new Point(8, 196),
                Size = new Size(360, 120),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            _btnAddFolder = new Button
            {
                Text = "Add folder…",
                Location = new Point(8, 324),
                Size = new Size(100, 28)
            };
            _btnAddFolder.Click += OnAddFolderClick;

            _btnRemoveFolder = new Button
            {
                Text = "Remove",
                Location = new Point(116, 324),
                Size = new Size(80, 28)
            };
            _btnRemoveFolder.Click += OnRemoveFolderClick;

            var lblFolderHelp = new Label
            {
                Text = "Leave empty to scan all of OneDrive. Example: Pictures/Holidays",
                Location = new Point(8, 358),
                Size = new Size(360, 32),
                ForeColor = SystemColors.GrayText,
                AutoSize = false
            };

            rightPanel.Controls.AddRange(new Control[]
            {
                lblName, _txtDisplayName,
                lblEdgeProfile, _cmbEdgeProfile,
                lblWeight, _numWeight, lblWeightHelp,
                lblFolders, _folderList,
                _btnAddFolder, _btnRemoveFolder, lblFolderHelp
            });

            layout.Controls.Add(leftPanel, 0, 0);
            layout.Controls.Add(rightPanel, 1, 0);
            page.Controls.Add(layout);
            return page;
        }

        // ══════════════════════════════════════════════════════════════════════
        // FILTERS TAB
        // ══════════════════════════════════════════════════════════════════════

        private TabPage BuildFiltersTab()
        {
            var page = new TabPage("Filters");
            var outer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };

            var lblInfo = new Label
            {
                Text = "Filters apply per account. Rules are checked top-to-bottom — first match wins.\n" +
                       "Select an account on the Accounts tab first.",
                Location = new Point(0, 0),
                Size = new Size(620, 36),
                AutoSize = false
            };

            _filterList = new ListBox
            {
                Location = new Point(0, 44),
                Size = new Size(640, 130),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _filterList.SelectedIndexChanged += OnFilterSelected;

            var btnAdd = new Button { Text = "Add rule", Location = new Point(0, 182), Size = new Size(90, 28) };
            btnAdd.Click += OnAddFilterClick;
            var btnRemove = new Button { Text = "Remove", Location = new Point(98, 182), Size = new Size(80, 28) };
            btnRemove.Click += OnRemoveFilterClick;
            var btnUp = new Button { Text = "▲", Location = new Point(186, 182), Size = new Size(36, 28) };
            btnUp.Click += (_, _) => MoveFilter(-1);
            var btnDown = new Button { Text = "▼", Location = new Point(226, 182), Size = new Size(36, 28) };
            btnDown.Click += (_, _) => MoveFilter(+1);

            var editorBox = new GroupBox
            {
                Text = "Edit selected rule",
                Location = new Point(0, 220),
                Size = new Size(640, 200),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };

            var l1 = new Label { Text = "Name:", Location = new Point(8, 24), AutoSize = true };
            _txtFilterName = new TextBox { Location = new Point(8, 42), Width = 260 };

            var l2 = new Label { Text = "Action:", Location = new Point(280, 24), AutoSize = true };
            _cmbAction = new ComboBox
            {
                Location = new Point(280, 42),
                Width = 110,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cmbAction.Items.AddRange(new object[] { "Include", "Exclude" });
            _cmbAction.SelectedIndex = 1;

            var l3 = new Label { Text = "Pattern type:", Location = new Point(8, 74), AutoSize = true };
            _cmbPatternType = new ComboBox
            {
                Location = new Point(8, 92),
                Width = 110,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cmbPatternType.Items.AddRange(new object[] { "Glob", "Regex" });
            _cmbPatternType.SelectedIndex = 0;

            var l4 = new Label { Text = "Pattern:", Location = new Point(130, 74), AutoSize = true };
            _txtPattern = new TextBox { Location = new Point(130, 92), Width = 480 };

            var l5 = new Label
            {
                Text = "Glob examples:  *.arw   |   */private/*   |   **/*.arw",
                Location = new Point(130, 116),
                AutoSize = true,
                ForeColor = SystemColors.GrayText
            };

            _chkFilterEnabled = new CheckBox
            {
                Text = "Rule enabled",
                Location = new Point(8, 148),
                AutoSize = true,
                Checked = true
            };

            var btnApply = new Button { Text = "Apply", Location = new Point(530, 144), Size = new Size(80, 28) };
            btnApply.Click += OnApplyFilterClick;

            editorBox.Controls.AddRange(new Control[]
            {
                l1, _txtFilterName, l2, _cmbAction,
                l3, _cmbPatternType, l4, _txtPattern, l5,
                _chkFilterEnabled, btnApply
            });

            outer.Controls.AddRange(new Control[]
            {
                lblInfo, _filterList,
                btnAdd, btnRemove, btnUp, btnDown,
                editorBox
            });
            page.Controls.Add(outer);
            return page;
        }

        // ══════════════════════════════════════════════════════════════════════
        // OPTIONS TAB
        // ══════════════════════════════════════════════════════════════════════

        private TabPage BuildOptionsTab()
        {
            var page = new TabPage("Options");
            var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16) };

            int y = 16;

            panel.Controls.Add(MakeLabel("Slide duration (seconds):", 0, y));
            _numSlideDuration = MakeNumeric(240, y, 5, 3600, 5);
            panel.Controls.Add(_numSlideDuration);
            y += 40;

            panel.Controls.Add(MakeLabel("Images to pre-download:", 0, y));
            _numPrefetch = MakeNumeric(240, y, 1, 20, 1);
            panel.Controls.Add(_numPrefetch);
            y += 40;

            panel.Controls.Add(MakeLabel("Disk cache limit (MB):", 0, y));
            _numCacheSize = MakeNumeric(240, y, 50, 10000, 50);
            panel.Controls.Add(_numCacheSize);
            y += 40;

            panel.Controls.Add(MakeLabel("Transition style:", 0, y));
            _cmbTransition = new ComboBox
            {
                Location = new Point(240, y),
                Width = 160,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cmbTransition.Items.AddRange(new object[] { "None", "Cross-fade", "Slide left", "Slide up" });
            panel.Controls.Add(_cmbTransition);
            y += 40;

            panel.Controls.Add(MakeLabel("Transition duration (ms):", 0, y));
            _numTransitionMs = MakeNumeric(240, y, 0, 3000, 100);
            panel.Controls.Add(_numTransitionMs);
            y += 40;

            _chkRunOnStartup = new CheckBox
            {
                Text = "Start CloudFrame when Windows starts",
                Location = new Point(0, y),
                AutoSize = true
            };
            panel.Controls.Add(_chkRunOnStartup);

            page.Controls.Add(panel);

            // Populate values
            _numSlideDuration.Value = Math.Max(5, Math.Min(3600, _settings.SlideDurationSeconds));
            _numPrefetch.Value = Math.Max(1, Math.Min(20, _settings.PrefetchCount));
            _numCacheSize.Value = Math.Max(50, Math.Min(10000, _settings.DiskCacheLimitMb));
            _cmbTransition.SelectedIndex = (int)_settings.Transition;
            _numTransitionMs.Value = Math.Max(0, Math.Min(3000, _settings.TransitionDurationMs));
            _chkRunOnStartup.Checked = _settings.RunOnStartup;

            return page;
        }

        // ══════════════════════════════════════════════════════════════════════
        // POPULATION
        // ══════════════════════════════════════════════════════════════════════

        private void PopulateAccountList()
        {
            _accountList.Items.Clear();
            foreach (var a in _accounts)
                _accountList.Items.Add(a.DisplayName.Length > 0 ? a.DisplayName : "(unnamed)");
        }

        private void PopulateOptions() { /* values set inline in BuildOptionsTab */ }

        private void PopulateFolderList(AccountConfig account)
        {
            _folderList.Items.Clear();
            foreach (var f in account.RootFolders)
                _folderList.Items.Add(f);
        }

        private void PopulateFilterList(AccountConfig? account)
        {
            _filterList.Items.Clear();
            if (account is null) return;
            foreach (var r in account.FilterRules)
                _filterList.Items.Add(FormatRule(r));
        }

        // ══════════════════════════════════════════════════════════════════════
        // EVENT HANDLERS — ACCOUNTS
        // ══════════════════════════════════════════════════════════════════════

        private void OnAccountSelected(object? sender, EventArgs e)
        {
            int idx = _accountList.SelectedIndex;
            if (idx < 0) return;
            var acc = _accounts[idx];
            _txtDisplayName.Text = acc.DisplayName;
            _numWeight.Value = (decimal)Math.Max(0.1, Math.Min(10.0, acc.SelectionWeight));
            PopulateFolderList(acc);
            PopulateFilterList(acc);
            SelectEdgeProfileForAccount(acc);
        }

        private void OnAddAccountClick(object? sender, EventArgs e)
        {
            var acc = new AccountConfig
            {
                DisplayName = "My OneDrive",
                ProviderType = "OneDrive",
                IsEnabled = true
            };
            _accounts.Add(acc);
            _accountList.Items.Add(acc.DisplayName);
            _accountList.SelectedIndex = _accounts.Count - 1;
        }

        private void OnRemoveAccountClick(object? sender, EventArgs e)
        {
            int idx = _accountList.SelectedIndex;
            if (idx < 0) return;
            if (MessageBox.Show($"Remove account \"{_accounts[idx].DisplayName}\"?",
                "CloudFrame", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            _accounts.RemoveAt(idx);
            _accountList.Items.RemoveAt(idx);
        }

        private void OnSignInClick(object? sender, EventArgs e)
        {
            int idx = _accountList.SelectedIndex;
            if (idx < 0) { MessageBox.Show("Select an account first.", "CloudFrame"); return; }

            var acc = _accounts[idx];
            string? edgeFolder = null;
            if (_cmbEdgeProfile.SelectedItem is EdgeProfileItem ep && ep.FolderName is not null)
                edgeFolder = ep.FolderName;
            var provider = new OneDriveProvider(acc.AccountId, acc.DisplayName, Program.HttpClient, edgeProfileFolder: edgeFolder);

            _ = Task.Run(async () =>
            {
                bool ok = await provider.SignInInteractiveAsync();
                BeginInvoke(() =>
                {
                    MessageBox.Show(this,
                        ok ? "Signed in successfully." : "Sign-in was cancelled or failed.",
                        "CloudFrame",
                        MessageBoxButtons.OK,
                        ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                });
            });
        }

        private void OnDisplayNameChanged(object? sender, EventArgs e)
        {
            int idx = _accountList.SelectedIndex;
            if (idx < 0) return;
            _accounts[idx].DisplayName = _txtDisplayName.Text;
            _accountList.Items[idx] = _txtDisplayName.Text.Length > 0 ? _txtDisplayName.Text : "(unnamed)";
        }

        private void OnWeightChanged(object? sender, EventArgs e)
        {
            int idx = _accountList.SelectedIndex;
            if (idx < 0) return;
            _accounts[idx].SelectionWeight = (double)_numWeight.Value;
        }

        private void PopulateEdgeProfiles()
        {
            _cmbEdgeProfile.Items.Clear();
            _cmbEdgeProfile.Items.Add(new EdgeProfileItem(null, "(System default browser)"));

            var profiles = CloudFrame.Providers.OneDrive.EdgeProfileDetector.GetProfiles();
            foreach (var p in profiles)
                _cmbEdgeProfile.Items.Add(new EdgeProfileItem(p.FolderName, p.DisplayName));

            _cmbEdgeProfile.SelectedIndex = 0;
        }

        private void SelectEdgeProfileForAccount(AccountConfig acc)
        {
            if (!acc.ProviderSettings.TryGetValue(ProviderFactory.EdgeProfileKey, out string? folder))
            {
                _cmbEdgeProfile.SelectedIndex = 0;
                return;
            }
            for (int i = 0; i < _cmbEdgeProfile.Items.Count; i++)
            {
                if (_cmbEdgeProfile.Items[i] is EdgeProfileItem item && item.FolderName == folder)
                {
                    _cmbEdgeProfile.SelectedIndex = i;
                    return;
                }
            }
            _cmbEdgeProfile.SelectedIndex = 0;
        }

        private void OnEdgeProfileChanged(object? sender, EventArgs e)
        {
            int idx = _accountList.SelectedIndex;
            if (idx < 0) return;
            if (_cmbEdgeProfile.SelectedItem is not EdgeProfileItem item) return;

            if (item.FolderName is null)
                _accounts[idx].ProviderSettings.Remove(ProviderFactory.EdgeProfileKey);
            else
                _accounts[idx].ProviderSettings[ProviderFactory.EdgeProfileKey] = item.FolderName;
        }

        // Simple wrapper so the combobox shows a friendly name but we can
        // retrieve the folder name for storage.
        private sealed record EdgeProfileItem(string? FolderName, string DisplayName)
        {
            public override string ToString() => DisplayName;
        }

        private void OnAddFolderClick(object? sender, EventArgs e)
        {
            int idx = _accountList.SelectedIndex;
            if (idx < 0) { MessageBox.Show("Select an account first.", "CloudFrame"); return; }

            var acc = _accounts[idx];

            // Get a valid access token for this account.
            string? token = GetAccessToken(acc);
            if (token is null)
            {
                MessageBox.Show(
                    "Please sign in to this account before browsing folders.",
                    "CloudFrame", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var picker = new OneDriveFolderPicker(token, Program.HttpClient);
            if (picker.ShowDialog() != DialogResult.OK) return;

            // SelectedPath is "" for root, or a relative path like "Pictures/Holidays".
            string folder = picker.SelectedPath ?? "";
            if (acc.RootFolders.Contains(folder)) return;   // already added

            acc.RootFolders.Add(folder);
            _folderList.Items.Add(folder.Length == 0 ? "(OneDrive root)" : folder);
        }

        /// <summary>
        /// Returns a valid access token for the given account if the user has
        /// already signed in, otherwise null.
        /// </summary>
        private static string? GetAccessToken(AccountConfig acc)
        {
            acc.ProviderSettings.TryGetValue(ProviderFactory.EdgeProfileKey, out string? edgeFolder);
            var auth = new CloudFrame.Providers.OneDrive.MsalAuthManager(
                acc.AccountId, edgeFolder);

            // Try silent auth — returns quickly if a cached token exists.
            var task = auth.EnsureAuthenticatedAsync();
            task.Wait(TimeSpan.FromSeconds(10));
            if (!task.Result) return null;

            return auth.AccessToken;
        }

        private void OnRemoveFolderClick(object? sender, EventArgs e)
        {
            int ai = _accountList.SelectedIndex, fi = _folderList.SelectedIndex;
            if (ai < 0 || fi < 0) return;
            _accounts[ai].RootFolders.RemoveAt(fi);
            _folderList.Items.RemoveAt(fi);
        }

        // ══════════════════════════════════════════════════════════════════════
        // EVENT HANDLERS — FILTERS
        // ══════════════════════════════════════════════════════════════════════

        private void OnFilterSelected(object? sender, EventArgs e)
        {
            int ai = _accountList.SelectedIndex, fi = _filterList.SelectedIndex;
            if (ai < 0 || fi < 0) return;
            var rule = _accounts[ai].FilterRules[fi];
            _txtFilterName.Text = rule.Name;
            _cmbAction.SelectedIndex = (int)rule.Action;
            _cmbPatternType.SelectedIndex = (int)rule.PatternType;
            _txtPattern.Text = rule.Pattern;
            _chkFilterEnabled.Checked = rule.IsEnabled;
        }

        private void OnAddFilterClick(object? sender, EventArgs e)
        {
            int idx = _accountList.SelectedIndex;
            if (idx < 0) { MessageBox.Show("Select an account first.", "CloudFrame"); return; }

            var rule = ShowFilterDialog(null);
            if (rule is null) return;

            _accounts[idx].FilterRules.Add(rule);
            _filterList.Items.Add(FormatRule(rule));
            _filterList.SelectedIndex = _filterList.Items.Count - 1;
        }

        /// <summary>
        /// Shows a self-contained Add/Edit rule dialog.
        /// Pass null for a new rule, or an existing rule to edit it.
        /// Returns the configured rule, or null if cancelled.
        /// </summary>
        private FilterRule? ShowFilterDialog(FilterRule? existing)
        {
            using var dlg = new Form
            {
                Text = existing is null ? "Add Filter Rule" : "Edit Filter Rule",
                Size = new Size(460, 280),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen,
                TopMost = true,
                MaximizeBox = false,
                MinimizeBox = false
            };

            // Name
            var lblName = new Label { Text = "Rule name:", Location = new Point(12, 14), AutoSize = true };
            var txtName = new TextBox { Location = new Point(12, 32), Width = 420, Text = existing?.Name ?? "" };

            // Action
            var lblAction = new Label { Text = "Action:", Location = new Point(12, 66), AutoSize = true };
            var cmbAction = new ComboBox
            {
                Location = new Point(12, 84),
                Width = 120,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbAction.Items.AddRange(new object[] { "Include", "Exclude" });
            cmbAction.SelectedIndex = existing is null ? 1 : (int)existing.Action;

            // Pattern type
            var lblType = new Label { Text = "Pattern type:", Location = new Point(148, 66), AutoSize = true };
            var cmbType = new ComboBox
            {
                Location = new Point(148, 84),
                Width = 100,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbType.Items.AddRange(new object[] { "Glob", "Regex" });
            cmbType.SelectedIndex = existing is null ? 0 : (int)existing.PatternType;

            // Pattern
            var lblPattern = new Label { Text = "Pattern:", Location = new Point(12, 116), AutoSize = true };
            var txtPattern = new TextBox
            {
                Location = new Point(12, 134),
                Width = 420,
                Text = existing?.Pattern ?? ""
            };

            // Help text
            var lblHelp = new Label
            {
                Text = "Glob: *.arw   |   */private/*   |   **/*.arw        Regex: (?i)\\.arw$",
                Location = new Point(12, 158),
                Size = new Size(420, 18),
                ForeColor = SystemColors.GrayText,
                AutoSize = false
            };

            // Enabled
            var chkEnabled = new CheckBox
            {
                Text = "Rule enabled",
                Location = new Point(12, 182),
                AutoSize = true,
                Checked = existing?.IsEnabled ?? true
            };

            // Buttons
            var btnOk = new Button
            {
                Text = existing is null ? "Add" : "Save",
                Location = new Point(264, 210),
                Size = new Size(80, 28),
                DialogResult = DialogResult.OK
            };
            var btnCancel = new Button
            {
                Text = "Cancel",
                Location = new Point(352, 210),
                Size = new Size(80, 28),
                DialogResult = DialogResult.Cancel
            };

            dlg.Controls.AddRange(new Control[]
            {
                lblName, txtName,
                lblAction, cmbAction,
                lblType, cmbType,
                lblPattern, txtPattern, lblHelp,
                chkEnabled,
                btnOk, btnCancel
            });
            dlg.AcceptButton = btnOk;
            dlg.CancelButton = btnCancel;

            if (dlg.ShowDialog() != DialogResult.OK) return null;

            if (string.IsNullOrWhiteSpace(txtPattern.Text))
            {
                MessageBox.Show("Please enter a pattern.", "CloudFrame",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }

            return new FilterRule
            {
                Name = txtName.Text.Trim().Length > 0 ? txtName.Text.Trim() : txtPattern.Text.Trim(),
                Action = (FilterAction)cmbAction.SelectedIndex,
                PatternType = (FilterPatternType)cmbType.SelectedIndex,
                Pattern = txtPattern.Text.Trim(),
                IsEnabled = chkEnabled.Checked
            };
        }

        private void OnRemoveFilterClick(object? sender, EventArgs e)
        {
            int ai = _accountList.SelectedIndex, fi = _filterList.SelectedIndex;
            if (ai < 0 || fi < 0) return;
            _accounts[ai].FilterRules.RemoveAt(fi);
            _filterList.Items.RemoveAt(fi);
        }

        private void MoveFilter(int dir)
        {
            int ai = _accountList.SelectedIndex, fi = _filterList.SelectedIndex;
            if (ai < 0 || fi < 0) return;
            var rules = _accounts[ai].FilterRules;
            int ni = fi + dir;
            if (ni < 0 || ni >= rules.Count) return;
            (rules[fi], rules[ni]) = (rules[ni], rules[fi]);
            PopulateFilterList(_accounts[ai]);
            _filterList.SelectedIndex = ni;
        }

        private void OnApplyFilterClick(object? sender, EventArgs e)
        {
            int ai = _accountList.SelectedIndex, fi = _filterList.SelectedIndex;
            if (ai < 0 || fi < 0) return;

            var existing = _accounts[ai].FilterRules[fi];
            var updated = ShowFilterDialog(existing);
            if (updated is null) return;

            _accounts[ai].FilterRules[fi] = updated;
            _filterList.Items[fi] = FormatRule(updated);

            // Refresh editor fields to reflect saved values.
            _txtFilterName.Text = updated.Name;
            _cmbAction.SelectedIndex = (int)updated.Action;
            _cmbPatternType.SelectedIndex = (int)updated.PatternType;
            _txtPattern.Text = updated.Pattern;
            _chkFilterEnabled.Checked = updated.IsEnabled;
        }

        // ══════════════════════════════════════════════════════════════════════
        // OK / SAVE
        // ══════════════════════════════════════════════════════════════════════

        private void OnOkClick(object? sender, EventArgs e)
        {
            _settings.Accounts.Clear();
            _settings.Accounts.AddRange(_accounts);
            _settings.SlideDurationSeconds = (int)_numSlideDuration.Value;
            _settings.PrefetchCount = (int)_numPrefetch.Value;
            _settings.DiskCacheLimitMb = (int)_numCacheSize.Value;
            _settings.Transition = (TransitionStyle)_cmbTransition.SelectedIndex;
            _settings.TransitionDurationMs = (int)_numTransitionMs.Value;
            _settings.RunOnStartup = _chkRunOnStartup.Checked;

            ApplyStartupRegistry(_settings.RunOnStartup);
            _ = _settingsService.SaveAsync(_settings);

            DialogResult = DialogResult.OK;
            Close();
        }

        private static void ApplyStartupRegistry(bool enable)
        {
            const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
            using var reg = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key, true);
            if (reg is null) return;
            if (enable)
                reg.SetValue("CloudFrame", $"\"{System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName}\"");
            else
                reg.DeleteValue("CloudFrame", false);
        }

        // ══════════════════════════════════════════════════════════════════════
        // HELPERS
        // ══════════════════════════════════════════════════════════════════════

        private static string FormatRule(FilterRule r)
        {
            string act = r.Action == FilterAction.Include ? "INCLUDE" : "EXCLUDE";
            string dis = r.IsEnabled ? "" : " [off]";
            return $"{act}  {r.Pattern}  — {r.Name}{dis}";
        }

        private string? Prompt(string title, string label)
        {
            using var dlg = new Form
            {
                Text = title,
                Size = new Size(440, 150),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen,
                TopMost = true,
                MaximizeBox = false,
                MinimizeBox = false
            };
            var lbl = new Label { Text = label, Location = new Point(8, 8), Size = new Size(410, 36), AutoSize = false };
            var txt = new TextBox { Location = new Point(8, 48), Width = 408 };
            var ok = new Button { Text = "OK", Location = new Point(248, 80), Size = new Size(80, 26), DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Location = new Point(336, 80), Size = new Size(80, 26), DialogResult = DialogResult.Cancel };
            dlg.Controls.AddRange(new Control[] { lbl, txt, ok, cancel });
            dlg.AcceptButton = ok; dlg.CancelButton = cancel;
            return dlg.ShowDialog() == DialogResult.OK ? txt.Text.Trim() : null;
        }

        private static Label MakeLabel(string text, int x, int y)
            => new() { Text = text, Location = new Point(x, y), AutoSize = true };

        private static NumericUpDown MakeNumeric(int x, int y, int min, int max, int increment)
            => new() { Location = new Point(x, y), Width = 100, Minimum = min, Maximum = max, Increment = increment };

        private static AccountConfig CloneAccount(AccountConfig src) => new()
        {
            AccountId = src.AccountId,
            DisplayName = src.DisplayName,
            ProviderType = src.ProviderType,
            IsEnabled = src.IsEnabled,
            SelectionWeight = src.SelectionWeight,
            RootFolders = new List<string>(src.RootFolders),
            FilterRules = new List<FilterRule>(src.FilterRules),
            ProviderSettings = new Dictionary<string, string>(src.ProviderSettings)
        };
    }
}