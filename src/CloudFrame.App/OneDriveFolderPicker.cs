using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CloudFrame.App
{
    /// <summary>
    /// Modal dialog that lets the user browse and select a OneDrive folder
    /// using a lazily-loaded TreeView. Subfolders are fetched from the
    /// Graph API only when the user expands a node, so the initial load
    /// is fast even for large OneDrive accounts.
    ///
    /// Returns the selected folder path relative to the OneDrive root
    /// (e.g. "Pictures/Holidays/2023"), or null if cancelled.
    /// </summary>
    public sealed class OneDriveFolderPicker : Form
    {
        // ── Graph API constants ────────────────────────────────────────────────
        private const string GraphBase = "https://graph.microsoft.com/v1.0";

        // ── State ──────────────────────────────────────────────────────────────
        private readonly string _accessToken;
        private readonly HttpClient _http;

        // ── Controls ───────────────────────────────────────────────────────────
        private readonly TreeView _tree;
        private readonly Label _lblSelected;
        private readonly Button _btnOk;
        private readonly Label _lblStatus;

        // Dummy child node inserted under every folder node so the expand
        // arrow shows before we know if the folder has children.
        private const string DummyTag = "__dummy__";

        /// <summary>
        /// The relative path the user selected, e.g. "Pictures/Holidays".
        /// Null if the dialog was cancelled or the root was selected.
        /// Empty string means the OneDrive root itself.
        /// </summary>
        public string? SelectedPath { get; private set; }

        public OneDriveFolderPicker(string accessToken, HttpClient http)
        {
            _accessToken = accessToken;
            _http = http;

            // ── Form ───────────────────────────────────────────────────────────
            Text = "Select OneDrive Folder";
            Size = new Size(480, 520);
            MinimumSize = new Size(380, 400);
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;

            // ── Instructions ───────────────────────────────────────────────────
            var lblInfo = new Label
            {
                Text = "Select a folder to scan for images. Expand folders to browse subfolders.",
                Location = new Point(8, 8),
                Size = new Size(448, 32),
                AutoSize = false
            };

            // ── Tree ───────────────────────────────────────────────────────────
            _tree = new TreeView
            {
                Location = new Point(8, 44),
                Size = new Size(448, 360),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                ShowRootLines = true,
                HideSelection = false,
                ImageList = BuildImageList()
            };
            _tree.BeforeExpand += OnBeforeExpand;
            _tree.AfterSelect += OnAfterSelect;
            _tree.NodeMouseDoubleClick += OnNodeDoubleClick;

            // ── Selected path label ────────────────────────────────────────────
            _lblSelected = new Label
            {
                Text = "Selected: (OneDrive root)",
                Location = new Point(8, 412),
                Size = new Size(448, 20),
                AutoSize = false,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                ForeColor = SystemColors.GrayText
            };

            // ── Status label (shown during loading) ────────────────────────────
            _lblStatus = new Label
            {
                Text = "",
                Location = new Point(8, 432),
                Size = new Size(448, 16),
                AutoSize = false,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                ForeColor = SystemColors.GrayText
            };

            // ── Buttons ────────────────────────────────────────────────────────
            // Add Bottom panel BEFORE other docked controls.
            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 44,
                Padding = new Padding(8, 8, 8, 4)
            };

            var btnCancel = new Button
            {
                Text = "Cancel",
                Size = new Size(88, 28),
                DialogResult = DialogResult.Cancel
            };

            _btnOk = new Button
            {
                Text = "Select",
                Size = new Size(88, 28),
                Enabled = false
            };
            _btnOk.Click += OnOkClick;

            buttonPanel.Controls.Add(btnCancel);
            buttonPanel.Controls.Add(_btnOk);

            Controls.Add(buttonPanel);
            Controls.Add(lblInfo);
            Controls.Add(_tree);
            Controls.Add(_lblSelected);
            Controls.Add(_lblStatus);

            AcceptButton = _btnOk;
            CancelButton = btnCancel;

            // Load the root node asynchronously once the form is shown.
            Load += (_, _) => _ = LoadRootAsync();
        }

        // ── Root loading ───────────────────────────────────────────────────────

        private async Task LoadRootAsync()
        {
            SetStatus("Loading OneDrive folders…");
            _tree.Nodes.Clear();

            // Add the root node representing the OneDrive root itself.
            var rootNode = new TreeNode("OneDrive (root)")
            {
                Tag = "",          // empty string = root path
                ImageIndex = 0,
                SelectedImageIndex = 0
            };

            _tree.Nodes.Add(rootNode);

            // Load root's children immediately so the user sees something.
            await LoadChildrenAsync(rootNode, "root");

            rootNode.Expand();
            _tree.SelectedNode = rootNode;
            SetStatus("");
        }

        // ── Lazy expand ────────────────────────────────────────────────────────

        private void OnBeforeExpand(object? sender, TreeViewCancelEventArgs e)
        {
            var node = e.Node;
            if (node is null) return;

            // If the only child is our dummy placeholder, replace it with
            // real children fetched from Graph API.
            if (node.Nodes.Count == 1 && node.Nodes[0].Tag as string == DummyTag)
            {
                node.Nodes.Clear();
                string itemRef = BuildItemRef(node.Tag as string ?? "");
                _ = LoadChildrenAsync(node, itemRef);
            }
        }

        private async Task LoadChildrenAsync(TreeNode parentNode, string itemRef)
        {
            SetStatus("Loading…");

            try
            {
                var folders = await FetchFoldersAsync(itemRef);

                // Must update TreeView on UI thread.
                if (InvokeRequired)
                {
                    Invoke(() => PopulateNode(parentNode, folders));
                }
                else
                {
                    PopulateNode(parentNode, folders);
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Error: {ex.Message}");
            }
            finally
            {
                SetStatus("");
            }
        }

        private void PopulateNode(TreeNode parent, List<FolderItem> folders)
        {
            foreach (var folder in folders)
            {
                // Build the full relative path for this node.
                string parentPath = parent.Tag as string ?? "";
                string childPath = parentPath.Length == 0
                    ? folder.Name
                    : $"{parentPath}/{folder.Name}";

                var node = new TreeNode(folder.Name)
                {
                    Tag = childPath,
                    ImageIndex = 0,
                    SelectedImageIndex = 0
                };

                // Add a dummy child so the expand arrow appears.
                // We'll replace it with real children when the user expands.
                if (folder.ChildFolderCount > 0)
                {
                    node.Nodes.Add(new TreeNode("Loading…") { Tag = DummyTag });
                }

                parent.Nodes.Add(node);
            }
        }

        // ── Selection ──────────────────────────────────────────────────────────

        private void OnAfterSelect(object? sender, TreeViewEventArgs e)
        {
            if (e.Node is null) return;

            string path = e.Node.Tag as string ?? "";
            SelectedPath = path;

            _lblSelected.Text = path.Length == 0
                ? "Selected: OneDrive root (scan everything)"
                : $"Selected: {path}";

            _btnOk.Enabled = true;
        }

        private void OnNodeDoubleClick(object? sender, TreeNodeMouseClickEventArgs e)
        {
            if (_btnOk.Enabled)
                OnOkClick(sender, e);
        }

        private void OnOkClick(object? sender, EventArgs e)
        {
            DialogResult = DialogResult.OK;
            Close();
        }

        // ── Graph API ──────────────────────────────────────────────────────────

        private async Task<List<FolderItem>> FetchFoldersAsync(string itemRef)
        {
            // itemRef is either "root" or "items/{id}" — but here we use
            // path-based addressing for simplicity since we track relative paths.
            string url = itemRef == "root"
                ? $"{GraphBase}/me/drive/root/children?$select=id,name,folder&$top=200"
                : $"{GraphBase}/me/drive/root:/{Uri.EscapeDataString(itemRef).Replace("%2F", "/")}" +
                  $":/children?$select=id,name,folder&$top=200";

            var results = new List<FolderItem>();
            string? nextLink = url;

            while (nextLink is not null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, nextLink);
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", _accessToken);

                using var response = await _http.SendAsync(request);

                if (!response.IsSuccessStatusCode) break;

                await using var stream = await response.Content.ReadAsStreamAsync();
                var page = await JsonSerializer.DeserializeAsync<GraphPage>(stream, s_jsonOptions);

                if (page?.Value is null) break;

                foreach (var item in page.Value)
                {
                    // Only include folders (items with a "folder" facet).
                    if (item.Folder is not null)
                    {
                        results.Add(new FolderItem(
                            item.Name,
                            item.Folder.ChildCount));
                    }
                }

                nextLink = page.ODataNextLink;
            }

            // Sort alphabetically for easier browsing.
            results.Sort((a, b) =>
                string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            return results;
        }

        private static string BuildItemRef(string relativePath)
            => relativePath.Length == 0 ? "root" : relativePath;

        // ── Helpers ────────────────────────────────────────────────────────────

        private void SetStatus(string message)
        {
            if (InvokeRequired) { Invoke(() => SetStatus(message)); return; }
            _lblStatus.Text = message;
        }

        private static ImageList BuildImageList()
        {
            // Simple folder icon drawn programmatically — no external resources needed.
            var list = new ImageList { ImageSize = new Size(16, 16) };
            using var bmp = new Bitmap(16, 16);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            // Draw a simple folder shape.
            g.FillRectangle(Brushes.Goldenrod, 0, 5, 14, 9);
            g.FillRectangle(Brushes.Goldenrod, 0, 3, 6, 4);
            list.Images.Add(new Bitmap(bmp));
            return list;
        }

        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        // ── Graph API DTOs ─────────────────────────────────────────────────────

        private sealed class GraphPage
        {
            [JsonPropertyName("value")]
            public List<GraphItem>? Value { get; init; }

            [JsonPropertyName("@odata.nextLink")]
            public string? ODataNextLink { get; init; }
        }

        private sealed class GraphItem
        {
            [JsonPropertyName("id")]
            public string Id { get; init; } = string.Empty;

            [JsonPropertyName("name")]
            public string Name { get; init; } = string.Empty;

            [JsonPropertyName("folder")]
            public FolderFacet? Folder { get; init; }
        }

        private sealed class FolderFacet
        {
            [JsonPropertyName("childCount")]
            public int ChildCount { get; init; }
        }

        private sealed record FolderItem(string Name, int ChildFolderCount);
    }
}