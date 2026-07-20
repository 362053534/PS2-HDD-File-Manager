using System.Runtime.InteropServices;
using System.Text;
using PfsExplorerLite.Models;
using PfsExplorerLite.Services;

namespace PfsExplorerLite;

internal sealed class MainForm : Form
{
    private const uint WmDropFiles = 0x0233;
    private const uint WmCopyData = 0x004A;
    private const uint WmCopyGlobalData = 0x0049;
    private const uint MsgfltAllow = 1;

    private readonly PfsShellClient _pfs;
    private readonly NbdClient _nbd;
    private readonly DiskDiscovery _diskDiscovery;
    private readonly ComboBox _deviceCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 549 };
    private readonly TextBox _pathBox = new() { ReadOnly = true, Width = 510, Text = "/" };
    private readonly ListView _entries = new BrowserListView { View = View.Details, FullRowSelect = true, MultiSelect = true, LabelEdit = true, Dock = DockStyle.Fill };
    private readonly ContextMenuStrip _entryMenu = new();
    private readonly ToolStripMenuItem _copyMenuItem = new("复制");
    private readonly ToolStripMenuItem _deleteMenuItem = new("删除");
    private readonly ToolStripMenuItem _renameMenuItem = new("重命名");
    private readonly ToolStripMenuItem _pasteMenuItem = new("粘贴");
    private readonly ToolStripMenuItem _newDirectoryMenuItem = new("新建目录");
    private readonly ToolStripMenuItem _refreshMenuItem = new("刷新");
    private readonly ToolStripSeparator _entryMenuSeparator = new();
    private readonly ToolStripSeparator _entryMenuSeparator2 = new();
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9F) };
    private string _currentPath = "/";
    private string? _activePartition;

    public MainForm()
    {
        var toolsDirectory = Path.Combine(AppContext.BaseDirectory, "tools");
        _pfs = new PfsShellClient(toolsDirectory);
        _nbd = new NbdClient(toolsDirectory);
        _diskDiscovery = new DiskDiscovery(toolsDirectory);

        Text = "PS2 HDD File Manager v1.0 — 峰哥原创，免费发布";
        MinimumSize = new Size(960, 660);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        // 提权进程需在创建窗口前放行拖放消息，否则资源管理器拖入会被 UIPI 拦截。
        AllowDragDropFromLowerIntegrityProcessWide();
        BuildUi();
        Shown += async (_, _) => await RefreshDisksAsync();
    }

    private void BuildUi()
    {
        _entries.Columns.Add("名称", 310);
        _entries.Columns.Add("类型", 90);
        _entries.Columns.Add("pfsshell 详情", 420);
        _entries.MouseDoubleClick += async (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                await OpenSelectedAsync();
        };
        _entries.BeforeLabelEdit += (_, e) =>
        {
            // 分区列表不允许就地重命名；仅已打开的 PFS 目录可改名。
            if (_activePartition is null || _entries.Items[e.Item].Tag is not PfsEntry)
                e.CancelEdit = true;
        };
        _entries.AfterLabelEdit += (_, e) =>
        {
            if (e.Label is null) return;
            var item = _entries.Items[e.Item];
            if (item.Tag is not PfsEntry entry)
            {
                e.CancelEdit = true;
                return;
            }
            var newName = e.Label.Trim();
            // 先恢复原名，成功后再刷新列表，避免失败时界面残留错误名称。
            e.CancelEdit = true;
            if (string.IsNullOrWhiteSpace(newName) || newName == PfsNameEncoding.ToDisplay(entry.Name)) return;
            BeginInvoke(async () => await ApplyRenameAsync(entry.Name, newName));
        };
        _entries.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var item = _entries.GetItemAt(e.X, e.Y);
            if (item is null)
            {
                _entries.SelectedItems.Clear();
                return;
            }
            if (!item.Selected)
            {
                _entries.SelectedItems.Clear();
                item.Selected = true;
            }
        };
        // 入站不用 OLE AllowDrop（提权下常显示禁止图标）；改走 WM_DROPFILES。
        ((BrowserListView)_entries).FilesDropped += async paths =>
        {
            if (_activePartition is null)
            {
                ShowWarning("请先双击打开一个 PFS 分区，再拖入文件。");
                return;
            }
            var items = paths.Where(path => File.Exists(path) || Directory.Exists(path)).ToArray();
            if (items.Length == 0) return;
            await ImportLocalItemsAsync(items);
        };
        _entries.ItemDrag += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || _activePartition is null || SelectedEntries().Count == 0)
                return;
            if (!TryCreateWindowsTransferDataObject(out var dataObject, out string _))
                return;
            _entries.DoDragDrop(dataObject, DragDropEffects.Copy);
        };
        _entries.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.F2)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                await RenameSelectedAsync();
                return;
            }
            if (!e.Control) return;
            if (e.KeyCode == Keys.A)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                foreach (ListViewItem item in _entries.Items)
                    item.Selected = true;
                return;
            }
            if (e.KeyCode == Keys.C)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                await CopySelectedToWindowsAsync();
                return;
            }
            if (e.KeyCode == Keys.V)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                await PasteImportAsync();
            }
        };
        _copyMenuItem.Click += async (_, _) => await CopySelectedToWindowsAsync();
        _deleteMenuItem.Click += async (_, _) => await DeleteSelectedAsync();
        _renameMenuItem.Click += async (_, _) => await RenameSelectedAsync();
        _pasteMenuItem.Click += async (_, _) => await PasteImportAsync();
        _newDirectoryMenuItem.Click += async (_, _) => await CreateDirectoryAsync();
        _refreshMenuItem.Click += async (_, _) => await RefreshBrowserAsync();
        _entryMenu.Items.AddRange(
        [
            _copyMenuItem,
            _deleteMenuItem,
            _renameMenuItem,
            _entryMenuSeparator,
            _pasteMenuItem,
            _newDirectoryMenuItem,
            _entryMenuSeparator2,
            _refreshMenuItem
        ]);
        _entryMenu.Opening += (_, e) =>
        {
            var selected = SelectedEntries();
            var onItem = selected.Count > 0;
            var inPartition = _activePartition is not null;
            _copyMenuItem.Visible = onItem;
            _deleteMenuItem.Visible = onItem;
            _renameMenuItem.Visible = onItem;
            _entryMenuSeparator.Visible = onItem;
            _copyMenuItem.Enabled = onItem;
            _deleteMenuItem.Enabled = onItem;
            _renameMenuItem.Enabled = selected.Count == 1;
            _pasteMenuItem.Enabled = inPartition && ClipboardHasImportableItems();
            _newDirectoryMenuItem.Enabled = inPartition;
            e.Cancel = false;
        };
        _entries.ContextMenuStrip = _entryMenu;
        _deviceCombo.SelectionChangeCommitted += async (_, _) => await LoadPartitionsAsync();

        var sourceBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        sourceBar.Controls.AddRange(
        [
            new Label { Text = "来源：", AutoSize = true, Padding = new Padding(0, 8, 0, 0) },
            _deviceCombo,
            MakeButton("刷新", async (_, _) => await RefreshDisksAsync()),
            MakeButton("加载硬盘镜像", SelectImage)
        ]);

        var nbdBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        var ipBox = new TextBox { Width = 170, PlaceholderText = "PS2 NBD IP，例如 192.168.1.10" };
        nbdBar.Controls.AddRange(
        [
            new Label { Text = "网络：", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, ipBox,
            MakeButton("连接 NBD", async (_, _) => await ConnectNbdAsync(ipBox.Text)),
            MakeButton("断开 NBD", async (_, _) => await DisconnectNbdAsync())
        ]);

        var actionBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        actionBar.Controls.AddRange(
        [
            MakeButton("上级目录", async (_, _) => await GoUpAsync()),
            _pathBox
        ]);

        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(10) };
        top.Controls.Add(sourceBar);
        top.Controls.Add(nbdBar);
        top.Controls.Add(actionBar);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var browserPage = new TabPage("PFS 浏览器");
        browserPage.Controls.Add(_entries);
        var logPage = new TabPage("诊断输出");
        logPage.Controls.Add(_log);
        tabs.TabPages.Add(browserPage);
        tabs.TabPages.Add(logPage);
        Controls.Add(tabs);
        Controls.Add(top);
    }

    private Button MakeButton(string text, EventHandler click) => new Button { Text = text, AutoSize = true, Margin = new Padding(4) }.Also(button => button.Click += click);

    private async Task RefreshDisksAsync()
    {
        await RunBusyAsync("正在扫描 PS2 APA 硬盘…", async () =>
        {
            var disks = await _diskDiscovery.GetPs2ApaDisksAsync(CancellationToken.None);
            _deviceCombo.Items.Clear();
            ResetPfsExplorer();
            foreach (var disk in disks)
                AddSourceDevice(disk.DevicePath, disk.DisplayName);
            if (_deviceCombo.Items.Count > 0)
            {
                _deviceCombo.SelectedIndex = 0;
                AppendLog($"发现 {disks.Count} 个 PS2 APA 硬盘。请选择目标后读取分区。");
            }
            else
            {
                _deviceCombo.Items.Add(new DeviceItem(string.Empty, "未检测到PS2硬盘"));
                _deviceCombo.SelectedIndex = 0;
                AppendLog("未检测到 PS2 APA 硬盘。");
            }
        });

        if ((_deviceCombo.SelectedItem as DeviceItem)?.Path is { Length: > 0 })
            await LoadPartitionsAsync();
    }

    private async void SelectImage(object? _, EventArgs __)
    {
        using var dialog = new OpenFileDialog { Title = "选择 PS2 HDD 原始镜像", Filter = "磁盘镜像|*.img;*.bin;*.raw|所有文件|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        SelectImageDevice(dialog.FileName, $"镜像 · {dialog.FileName}");
        ResetPfsExplorer();
        AppendLog($"已选择镜像：{dialog.FileName}");
        await LoadPartitionsAsync();
    }

    private void SelectImageDevice(string imagePath, string displayName)
    {
        AddSourceDevice(imagePath, displayName);
        _deviceCombo.SelectedIndex = _deviceCombo.Items.Count - 1;
    }

    private void AddSourceDevice(string path, string displayName)
    {
        for (var index = _deviceCombo.Items.Count - 1; index >= 0; index--)
        {
            if (_deviceCombo.Items[index] is DeviceItem item && string.IsNullOrWhiteSpace(item.Path))
                _deviceCombo.Items.RemoveAt(index);
        }
        _deviceCombo.Items.Add(new DeviceItem(path, displayName));
    }

    private void ResetPfsExplorer()
    {
        _entries.Items.Clear();
        _activePartition = null;
        _currentPath = "/";
        _pathBox.Text = "PFS 分区（双击查看）";
    }

    private async Task ConnectNbdAsync(string ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress)) { ShowWarning("请输入 PS2 的 NBD IP 地址。"); return; }
        await RunBusyAsync("正在连接 NBD…", async () =>
        {
            var result = await _nbd.MapAsync(ipAddress.Trim(), CancellationToken.None);
            AppendResult(result);
            if (result.ExitCode != 0) throw new InvalidOperationException("WNBD 映射失败，请查看诊断输出。");
            await RefreshDisksAsync();
            AppendLog("NBD 已映射。请在来源列表中选择新增的 PS2 APA 硬盘，再读取 PFS 分区。");
        });
    }

    private async Task DisconnectNbdAsync() => await RunBusyAsync("正在断开 NBD…", async () =>
    {
        var result = await _nbd.UnmapAsync(CancellationToken.None);
        AppendResult(result);
        if (result.ExitCode != 0) throw new InvalidOperationException("WNBD 断开失败，请查看诊断输出。");
        await RefreshDisksAsync();
    });

    private async Task LoadPartitionsAsync()
    {
        if (!TryGetDevice(out var device)) return;
        await RunBusyAsync("正在读取分区表…", async () =>
        {
            var result = await _pfs.ListPartitionsAsync(device, CancellationToken.None);
            AppendResult(result);
            var apaPartitions = PfsShellClient.ParsePartitionNames(result.StandardOutput);
            var partitions = await _pfs.FilterBrowsablePfsPartitionsAsync(device, apaPartitions, CancellationToken.None);
            _activePartition = null;
            _currentPath = "/";
            _entries.BeginUpdate();
            _entries.Items.Clear();
            foreach (var partition in partitions)
            {
                var item = new ListViewItem(partition) { Tag = new PfsEntry(partition, true, "可操作的 PFS 分区；双击查看") };
                item.SubItems.Add("PFS 分区");
                item.SubItems.Add("可操作的 PFS 分区；双击查看");
                _entries.Items.Add(item);
            }
            _entries.EndUpdate();
            _pathBox.Text = "PFS 分区（双击查看）";
            AppendLog(partitions.Count > 0
                ? $"识别到 {partitions.Count} 个可浏览 PFS 分区。双击分区开始浏览。"
                : "未找到可浏览的 PFS 分区。请查看诊断输出。");
        });
    }

    private async Task RefreshDirectoryAsync()
    {
        if (!TryGetPfsTarget(out var device, out var partition)) return;
        await RunBusyAsync("正在读取目录…", async () =>
        {
            var result = await _pfs.ListDirectoryAsync(device, partition, _currentPath, CancellationToken.None);
            AppendResult(result);
            if (!PfsShellClient.IsSuccessfulSession(result))
                throw new InvalidOperationException($"无法打开 PFS 分区“{partition}”，请查看诊断输出。");
            var entries = PfsShellClient.ParseDirectoryEntries(result.StandardOutput);
            _entries.BeginUpdate();
            _entries.Items.Clear();
            foreach (var entry in entries)
            {
                var displayName = PfsNameEncoding.ToDisplay(entry.Name);
                var item = new ListViewItem(displayName) { Tag = entry, ImageIndex = entry.IsDirectory ? 0 : 1 };
                item.SubItems.Add(entry.IsDirectory ? "目录" : "文件");
                item.SubItems.Add(PfsNameEncoding.ToDisplay(entry.Details));
                _entries.Items.Add(item);
            }
            _entries.EndUpdate();
            _pathBox.Text = $"{partition}:{PfsNameEncoding.ToDisplayPath(_currentPath)}";
            AppendLog($"目录 {(_pathBox.Text)}：{entries.Count} 项。");
        });
    }

    private Task RefreshBrowserAsync() => _activePartition is null ? LoadPartitionsAsync() : RefreshDirectoryAsync();

    private async Task OpenSelectedAsync()
    {
        if (SelectedEntry() is not { } entry) return;
        if (_activePartition is null)
        {
            _activePartition = entry.Name;
            _currentPath = "/";
            await RefreshDirectoryAsync();
            return;
        }

        if (!entry.IsDirectory) return;
        _currentPath = CombineRemotePath(_currentPath, entry.Name);
        await RefreshDirectoryAsync();
    }

    private async Task GoUpAsync()
    {
        if (_activePartition is null) return;
        var parts = _currentPath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count == 0)
        {
            await LoadPartitionsAsync();
            return;
        }
        parts.RemoveAt(parts.Count - 1);
        _currentPath = parts.Count == 0 ? "/" : "/" + string.Join('/', parts);
        await RefreshDirectoryAsync();
    }

    private async Task ExportSelectedAsync()
    {
        if (SelectedEntry() is not { IsDirectory: false } entry) { ShowWarning("请选择一个文件。目录导出将在后续版本加入。"); return; }
        if (!TryGetPfsTarget(out var device, out var partition)) return;
        using var dialog = new FolderBrowserDialog { Description = "选择导出到的本地目录" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await RunBusyAsync($"正在导出 {PfsNameEncoding.ToDisplay(entry.Name)}…", async () =>
        {
            var result = await _pfs.ExportAsync(device, partition, _currentPath, entry.Name, dialog.SelectedPath, CancellationToken.None);
            AppendResult(result);
            if (!PfsShellClient.IsSuccessfulSession(result)) throw new InvalidOperationException("导出失败，请查看诊断输出。");
        });
    }

    private async Task ImportFileAsync()
    {
        using var dialog = new OpenFileDialog { Title = "导入文件到当前 PFS 目录", Multiselect = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await ImportLocalItemsAsync(dialog.FileNames);
    }

    private async Task PasteImportAsync()
    {
        if (!Clipboard.ContainsFileDropList())
        {
            ShowWarning("Windows 剪贴板中没有可导入的文件。请先在资源管理器中复制文件。");
            return;
        }

        var paths = Clipboard.GetFileDropList().Cast<string>().ToArray();
        var items = paths.Where(path => File.Exists(path) || Directory.Exists(path)).ToArray();
        if (items.Length == 0)
        {
            ShowWarning("剪贴板中没有可导入的文件或目录。");
            return;
        }

        await ImportLocalItemsAsync(items);
    }

    private async Task ImportLocalItemsAsync(IEnumerable<string> localItems)
    {
        if (!TryGetPfsTarget(out var device, out var partition)) return;
        var items = localItems
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (items.Length == 0) return;

        var hasConflicts = false;
        await RunBusyAsync("正在扫描导入冲突…", async () =>
        {
            var cache = new Dictionary<string, Dictionary<string, PfsEntry>>(StringComparer.OrdinalIgnoreCase);
            hasConflicts = await HasImportConflictsAsync(items, device, partition, _currentPath, cache);
        });

        if (hasConflicts && !OverwriteConfirmation.Show(this))
            return;

        await RunBusyAsync($"正在导入 {items.Length} 个项目…", async () =>
        {
            var cache = new Dictionary<string, Dictionary<string, PfsEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
                await ImportLocalItemAsync(item, device, partition, _currentPath, cache, hasConflicts);
            await RefreshDirectoryAsync();
        });
    }

    // 允许非提权进程（资源管理器）向本提权窗口拖放文件。
    private static void AllowDragDropFromLowerIntegrityProcessWide()
    {
        ChangeWindowMessageFilter(WmDropFiles, MsgfltAdd);
        ChangeWindowMessageFilter(WmCopyData, MsgfltAdd);
        ChangeWindowMessageFilter(WmCopyGlobalData, MsgfltAdd);
    }

    private static void AllowDragDropFromLowerIntegrity(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;
        var filter = new ChangeFilterStruct { CbSize = (uint)Marshal.SizeOf<ChangeFilterStruct>() };
        ChangeWindowMessageFilterEx(hwnd, WmDropFiles, MsgfltAllow, ref filter);
        ChangeWindowMessageFilterEx(hwnd, WmCopyData, MsgfltAllow, ref filter);
        ChangeWindowMessageFilterEx(hwnd, WmCopyGlobalData, MsgfltAllow, ref filter);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ChangeFilterStruct
    {
        public uint CbSize;
        public uint ExtStatus;
    }

    private const uint MsgfltAdd = 1;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilter(uint message, uint dwFlag);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, ref ChangeFilterStruct changeFilterStruct);

    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, int cch);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr hDrop);

    /// <summary>
    /// 提权窗口用 WM_DROPFILES 接收资源管理器拖入；OLE AllowDrop 在 UIPI 下常只显示禁止图标。
    /// </summary>
    private sealed class BrowserListView : ListView
    {
        public event Func<string[], Task>? FilesDropped;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            AllowDragDropFromLowerIntegrity(Handle);
            DragAcceptFiles(Handle, true);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == (int)WmDropFiles)
            {
                var paths = QueryDroppedPaths(m.WParam);
                DragFinish(m.WParam);
                if (paths.Length > 0 && FilesDropped is not null)
                {
                    var handler = FilesDropped;
                    BeginInvoke(async () => await handler(paths));
                }
                return;
            }

            base.WndProc(ref m);
        }

        private static string[] QueryDroppedPaths(IntPtr hDrop)
        {
            var count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
            if (count == 0)
                return [];

            var paths = new string[count];
            var buffer = new StringBuilder(520);
            for (uint index = 0; index < count; index++)
            {
                var length = (int)DragQueryFile(hDrop, index, null, 0);
                buffer.EnsureCapacity(length + 1);
                buffer.Clear();
                DragQueryFile(hDrop, index, buffer, buffer.Capacity);
                paths[index] = buffer.ToString();
            }
            return paths;
        }
    }

    private Task CopySelectedToWindowsAsync()
    {
        if (_activePartition is null || SelectedEntries().Count == 0)
        {
            ShowWarning("请先打开 PFS 分区，并选择要复制的文件或目录。");
            return Task.CompletedTask;
        }

        if (!TryCreateWindowsTransferDataObject(out var dataObject, out var successLog))
            return Task.CompletedTask;

        try
        {
            if (dataObject is VirtualFileClipboardDataObject virtualObject)
                VirtualFileClipboard.Set(virtualObject);
            else if (dataObject is DataObject standardObject)
                VirtualFileClipboard.SetOwnedDataObject(standardObject);
            else
                Clipboard.SetDataObject(dataObject, copy: false);

            var formats = Clipboard.GetDataObject()?.GetFormats() ?? [];
            AppendLog($"Windows 剪贴板已注册格式：{string.Join(", ", formats)}");
            AppendLog(successLog);
        }
        catch (Exception ex)
        {
            AppendLog(ex.ToString());
            ShowWarning($"无法写入 Windows 剪贴板：{ex.Message}");
        }
        return Task.CompletedTask;
    }

    // 供 Ctrl+C / 拖出共用：构造延迟落地的虚拟文件 DataObject（仅 Copy）。
    private bool TryCreateWindowsTransferDataObject(out System.Windows.Forms.IDataObject dataObject, out string successLog)
    {
        dataObject = null!;
        successLog = string.Empty;
        var selected = SelectedEntries();
        if (selected.Count == 0 || _activePartition is null)
            return false;
        if (!TryGetPfsTarget(out var device, out var partition))
            return false;

        var sourcePath = _currentPath;
        var sourceItems = selected.Select(entry => new PfsClipboardSelection(entry.Name, entry.IsDirectory)).ToArray();
        var stagingDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PS2-HDD-File-Manager",
            "Clipboard",
            Guid.NewGuid().ToString("N"));

        if (sourceItems is [{ IsDirectory: false } singleFile])
        {
            // RelativePath 给资源管理器显示/落盘；Name 保持 ls 保真字节名供 pfsshell 使用。
            var deferredFile = new DeferredPfsFile(PfsNameEncoding.ToDisplay(singleFile.Name), sourcePath, singleFile.Name, false);
            dataObject = VirtualFileClipboard.CreateSingleFileDataObject(
                deferredFile,
                () => Task.Run(() => MaterializeVirtualClipboardFileAsync(device, partition, deferredFile, stagingDirectory)).GetAwaiter().GetResult());
            successLog = "已将 1 个 PFS 文件放入 Windows 延迟传输。目标请求时才会读取 PFS。";
            return true;
        }

        dataObject = new VirtualFileClipboardDataObject(
            () => Task.Run(() => BuildVirtualClipboardFilesAsync(device, partition, sourcePath, sourceItems)).GetAwaiter().GetResult(),
            file => Task.Run(() => MaterializeVirtualClipboardFileAsync(device, partition, file, stagingDirectory)).GetAwaiter().GetResult(),
            ex => ReportVirtualClipboardError(ex));
        successLog = $"已将 {sourceItems.Length} 个 PFS 项目放入 Windows 延迟传输。目标请求时才会读取 PFS。";
        return true;
    }

    private async Task CreateDirectoryAsync()
    {
        if (!TryGetPfsTarget(out var device, out var partition)) return;
        var name = Prompt.Show(this, "新建目录", "目录名称：");
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunBusyAsync("正在创建目录…", async () =>
        {
            var result = await _pfs.CreateDirectoryAsync(device, partition, _currentPath, name, CancellationToken.None);
            AppendResult(result);
            if (!PfsShellClient.IsSuccessfulSession(result)) throw new InvalidOperationException("创建目录失败，请查看诊断输出。");
            await RefreshDirectoryAsync();
        });
    }

    private Task RenameSelectedAsync()
    {
        if (_activePartition is null || _entries.SelectedItems.Count != 1 || _entries.SelectedItems[0].Tag is not PfsEntry)
        {
            ShowWarning("请先打开 PFS 分区，并选择要重命名的文件或目录。");
            return Task.CompletedTask;
        }
        _entries.FocusedItem = _entries.SelectedItems[0];
        _entries.SelectedItems[0].BeginEdit();
        return Task.CompletedTask;
    }

    private async Task ApplyRenameAsync(string oldName, string newName)
    {
        if (!TryGetPfsTarget(out var device, out var partition)) return;
        await RunBusyAsync("正在重命名…", async () =>
        {
            var result = await _pfs.RenameAsync(device, partition, _currentPath, oldName, newName, CancellationToken.None);
            AppendResult(result);
            if (!PfsShellClient.IsSuccessfulSession(result)) throw new InvalidOperationException("重命名失败，请查看诊断输出。");
            await RefreshDirectoryAsync();
        });
    }

    private async Task DeleteSelectedAsync()
    {
        var selected = SelectedEntries();
        if (!TryGetPfsTarget(out var device, out var partition) || selected.Count == 0)
        {
            ShowWarning("请先打开 PFS 分区，并选择一个或多个要删除的文件或目录。");
            return;
        }
        var hasDirectory = selected.Any(entry => entry.IsDirectory);
        var message = selected.Count == 1
            ? selected[0].IsDirectory
                ? $"确定永久删除 PFS 中的目录“{PfsNameEncoding.ToDisplay(selected[0].Name)}”及其全部内容？"
                : $"确定永久删除 PFS 中的文件“{PfsNameEncoding.ToDisplay(selected[0].Name)}”？"
            : hasDirectory
                ? $"确定永久删除选中的 {selected.Count} 个 PFS 项目？其中的目录会连同内容一并删除。"
                : $"确定永久删除选中的 {selected.Count} 个 PFS 文件？";
        if (MessageBox.Show(this, message, "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        await RunBusyAsync($"正在删除 {selected.Count} 个项目…", async () =>
        {
            var cache = new Dictionary<string, Dictionary<string, PfsEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in selected)
            {
                if (entry.IsDirectory)
                    await DeleteRemoteDirectoryAsync(device, partition, _currentPath, entry.Name, cache);
                else
                    await DeleteRemoteFileAsync(device, partition, _currentPath, entry.Name);
            }
            await RefreshDirectoryAsync();
        });
    }

    private async Task<bool> HasImportConflictsAsync(
        IEnumerable<string> localItems,
        string device,
        string partition,
        string remoteDirectory,
        Dictionary<string, Dictionary<string, PfsEntry>> cache)
    {
        var hasConflicts = false;
        foreach (var item in localItems)
            hasConflicts |= await HasImportConflictsAsync(item, device, partition, remoteDirectory, cache);
        return hasConflicts;
    }

    private async Task<bool> HasImportConflictsAsync(
        string localItem,
        string device,
        string partition,
        string remoteDirectory,
        Dictionary<string, Dictionary<string, PfsEntry>> cache)
    {
        var name = LocalItemName(localItem);
        // 本地 Unicode 名 → PFS 保真字节名，才能和 ls 缓存键、cd 路径一致。
        var shellName = PfsNameEncoding.ToShell(name);
        var entries = await GetRemoteEntriesAsync(device, partition, remoteDirectory, cache);
        var conflict = entries.TryGetValue(shellName, out var existing);
        if (!Directory.Exists(localItem) || existing is not { IsDirectory: true })
            return conflict;

        var childRemoteDirectory = CombineRemotePath(remoteDirectory, existing.Name);
        foreach (var child in Directory.EnumerateFileSystemEntries(localItem))
            conflict |= await HasImportConflictsAsync(child, device, partition, childRemoteDirectory, cache);
        return conflict;
    }

    private async Task ImportLocalItemAsync(
        string localItem,
        string device,
        string partition,
        string remoteDirectory,
        Dictionary<string, Dictionary<string, PfsEntry>> cache,
        bool overwrite)
    {
        var name = LocalItemName(localItem);
        var shellName = PfsNameEncoding.ToShell(name);
        var isDirectory = Directory.Exists(localItem);
        var entries = await GetRemoteEntriesAsync(device, partition, remoteDirectory, cache);
        entries.TryGetValue(shellName, out var existing);

        if (existing is not null && !overwrite)
            throw new InvalidOperationException($"目标中已存在“{name}”。请重新导入并选择全部覆盖。");

        if (isDirectory)
        {
            if (existing is { IsDirectory: false })
            {
                await DeleteRemoteFileAsync(device, partition, remoteDirectory, existing.Name);
                entries.Remove(shellName);
                existing = null;
            }
            if (existing is null)
            {
                await CreateRemoteDirectoryAsync(device, partition, remoteDirectory, name);
                entries[shellName] = new PfsEntry(shellName, true, string.Empty);
            }

            var childRemoteDirectory = CombineRemotePath(remoteDirectory, existing?.Name ?? shellName);
            foreach (var child in Directory.EnumerateFileSystemEntries(localItem))
                await ImportLocalItemAsync(child, device, partition, childRemoteDirectory, cache, overwrite);
            return;
        }

        if (existing is { IsDirectory: true })
        {
            await DeleteRemoteDirectoryAsync(device, partition, remoteDirectory, existing.Name, cache);
            entries.Remove(shellName);
        }
        else if (existing is not null)
        {
            await DeleteRemoteFileAsync(device, partition, remoteDirectory, existing.Name);
            entries.Remove(shellName);
        }

        var import = await _pfs.ImportAsync(device, partition, remoteDirectory, localItem, CancellationToken.None);
        AppendResult(import);
        if (!PfsShellClient.IsSuccessfulSession(import))
            throw new InvalidOperationException($"导入“{name}”失败，请查看诊断输出。");
        entries[shellName] = new PfsEntry(shellName, false, string.Empty);
    }

    private async Task<Dictionary<string, PfsEntry>> GetRemoteEntriesAsync(
        string device,
        string partition,
        string remoteDirectory,
        Dictionary<string, Dictionary<string, PfsEntry>> cache)
    {
        if (cache.TryGetValue(remoteDirectory, out var entries)) return entries;
        var result = await _pfs.ListDirectoryAsync(device, partition, remoteDirectory, CancellationToken.None);
        AppendResult(result);
        if (!PfsShellClient.IsSuccessfulSession(result))
            throw new InvalidOperationException($"无法读取目标目录“{remoteDirectory}”，请查看诊断输出。");
        entries = PfsShellClient.ParseDirectoryEntries(result.StandardOutput)
            .ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
        cache[remoteDirectory] = entries;
        return entries;
    }

    private async Task DeleteRemoteDirectoryAsync(
        string device,
        string partition,
        string parentDirectory,
        string name,
        Dictionary<string, Dictionary<string, PfsEntry>> cache)
    {
        var directory = CombineRemotePath(parentDirectory, name);
        var entries = await GetRemoteEntriesAsync(device, partition, directory, cache);
        foreach (var child in entries.Values.ToArray())
        {
            if (child.IsDirectory)
                await DeleteRemoteDirectoryAsync(device, partition, directory, child.Name, cache);
            else
                await DeleteRemoteFileAsync(device, partition, directory, child.Name);
        }
        var result = await _pfs.RemoveDirectoryAsync(device, partition, parentDirectory, name, CancellationToken.None);
        AppendResult(result);
        if (!PfsShellClient.IsSuccessfulSession(result))
            throw new InvalidOperationException($"无法删除目录“{name}”，请查看诊断输出。");
        cache.Remove(directory);
    }

    private async Task DeleteRemoteFileAsync(string device, string partition, string remoteDirectory, string name)
    {
        var result = await _pfs.DeleteAsync(device, partition, remoteDirectory, name, CancellationToken.None);
        AppendResult(result);
        if (!PfsShellClient.IsSuccessfulSession(result))
            throw new InvalidOperationException($"无法删除文件“{name}”，请查看诊断输出。");
    }

    private async Task CreateRemoteDirectoryAsync(string device, string partition, string remoteDirectory, string name)
    {
        var result = await _pfs.CreateDirectoryAsync(device, partition, remoteDirectory, name, CancellationToken.None);
        AppendResult(result);
        if (!PfsShellClient.IsSuccessfulSession(result))
            throw new InvalidOperationException($"无法创建目录“{name}”，请查看诊断输出。");
    }

    private async Task<IReadOnlyList<DeferredPfsFile>> BuildVirtualClipboardFilesAsync(
        string device,
        string partition,
        string sourcePath,
        IReadOnlyList<PfsClipboardSelection> selections)
    {
        var files = new List<DeferredPfsFile>();
        foreach (var selection in selections)
        {
            if (selection.IsDirectory)
                await AddVirtualDirectoryAsync(device, partition, CombineRemotePath(sourcePath, selection.Name), PfsNameEncoding.ToDisplay(selection.Name), files);
            else
                files.Add(new DeferredPfsFile(PfsNameEncoding.ToDisplay(selection.Name), sourcePath, selection.Name, false));
        }
        if (files.Count == 0) throw new InvalidOperationException("没有可复制的 PFS 项目。");
        return files;
    }

    private async Task AddVirtualDirectoryAsync(
        string device,
        string partition,
        string remoteDirectory,
        string relativePath,
        List<DeferredPfsFile> files)
    {
        files.Add(new DeferredPfsFile(relativePath, string.Empty, string.Empty, true));
        var result = await _pfs.ListDirectoryAsync(device, partition, remoteDirectory, CancellationToken.None);
        if (!PfsShellClient.IsSuccessfulSession(result))
            throw new InvalidOperationException($"无法读取 PFS 目录“{relativePath}”。连接可能已断开。");
        foreach (var entry in PfsShellClient.ParseDirectoryEntries(result.StandardOutput))
        {
            var childRelativePath = CombineRemotePath(relativePath, PfsNameEncoding.ToDisplay(entry.Name));
            if (entry.IsDirectory)
                await AddVirtualDirectoryAsync(device, partition, CombineRemotePath(remoteDirectory, entry.Name), childRelativePath, files);
            else
                files.Add(new DeferredPfsFile(childRelativePath, remoteDirectory, entry.Name, false));
        }
    }

    private async Task<string> MaterializeVirtualClipboardFileAsync(
        string device,
        string partition,
        DeferredPfsFile file,
        string stagingDirectory)
    {
        var relativeDirectory = Path.GetDirectoryName(file.RelativePath.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        var destination = Path.Combine(stagingDirectory, relativeDirectory);
        Directory.CreateDirectory(destination);
        var result = await _pfs.ExportAsync(device, partition, file.RemoteDirectory, file.Name, destination, CancellationToken.None);
        if (!PfsShellClient.IsSuccessfulSession(result))
            throw new InvalidOperationException($"无法读取 PFS 文件“{file.RelativePath}”。连接可能已断开。");
        var localPath = Path.Combine(destination, PfsNameEncoding.ToDisplay(file.Name));
        if (!File.Exists(localPath))
            throw new InvalidOperationException($"PFS 文件“{file.RelativePath}”未能导出。");
        return localPath;
    }

    private void ReportVirtualClipboardError(Exception error)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            AppendLog(error.ToString());
            ShowWarning($"粘贴失败：{error.Message}");
        });
    }

    private static string LocalItemName(string path) => Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private static bool ClipboardHasImportableItems()
    {
        if (!Clipboard.ContainsFileDropList()) return false;
        return Clipboard.GetFileDropList().Cast<string>().Any(path => File.Exists(path) || Directory.Exists(path));
    }

    private bool TryGetDevice(out string device)
    {
        device = (_deviceCombo.SelectedItem as DeviceItem)?.Path ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(device)) return true;
        ShowWarning("未检测到 PS2 APA 硬盘。请连接硬盘后刷新，或手动选择镜像文件。");
        return false;
    }

    private bool TryGetPfsTarget(out string device, out string partition)
    {
        partition = _activePartition ?? string.Empty;
        if (!TryGetDevice(out device)) return false;
        if (!string.IsNullOrWhiteSpace(partition)) return true;
        ShowWarning("请先在浏览区双击打开一个 PFS 分区。");
        return false;
    }

    private PfsEntry? SelectedEntry() => _entries.SelectedItems.Count == 1 ? _entries.SelectedItems[0].Tag as PfsEntry : null;
    private IReadOnlyList<PfsEntry> SelectedEntries() => _entries.SelectedItems.Cast<ListViewItem>().Select(item => item.Tag as PfsEntry).OfType<PfsEntry>().ToList();
    private static string CombineRemotePath(string parent, string child) => (parent.TrimEnd('/') + "/" + child).Replace("//", "/");
    private void AppendResult(ProcessResult result) => AppendLog(result.CombinedOutput);
    private void AppendLog(string message) => _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message.Trim()}{Environment.NewLine}");
    private void ShowWarning(string message) => MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private async Task RunBusyAsync(string status, Func<Task> action)
    {
        UseWaitCursor = true;
        Enabled = false;
        AppendLog(status);
        try { await action(); }
        catch (Exception ex) { AppendLog(ex.ToString()); ShowWarning(ex.Message); }
        finally { Enabled = true; UseWaitCursor = false; }
    }

    private sealed record DeviceItem(string Path, string Display) { public override string ToString() => Display; }
    private sealed record PfsClipboardSelection(string Name, bool IsDirectory);
}

internal static class ControlExtensions
{
    public static T Also<T>(this T control, Action<T> action) where T : Control { action(control); return control; }
}

internal static class Prompt
{
    public static string? Show(IWin32Window owner, string title, string label, string initialValue = "")
    {
        using var dialog = new Form { Text = title, ClientSize = new Size(420, 120), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false };
        var textBox = new TextBox { Left = 18, Top = 46, Width = 384, Text = initialValue };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Left = 246, Top = 82, Width = 75 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Left = 327, Top = 82, Width = 75 };
        dialog.Controls.AddRange([new Label { Text = label, Left = 18, Top = 18, AutoSize = true }, textBox, ok, cancel]);
        dialog.AcceptButton = ok; dialog.CancelButton = cancel;
        return dialog.ShowDialog(owner) == DialogResult.OK ? textBox.Text.Trim() : null;
    }
}

internal static class OverwriteConfirmation
{
    public static bool Show(IWin32Window owner)
    {
        using var dialog = new Form { Text = "发现同名项目", ClientSize = new Size(460, 132), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false };
        var label = new Label { Text = "目标 PFS 目录中发现同名文件或目录。\n选择“全部覆盖”将替换同名文件，并合并同名目录。", Left = 18, Top = 18, Width = 424, Height = 48 };
        var overwrite = new Button { Text = "全部覆盖", DialogResult = DialogResult.OK, Left = 250, Top = 82, Width = 92 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Left = 350, Top = 82, Width = 92 };
        dialog.Controls.AddRange([label, overwrite, cancel]);
        dialog.AcceptButton = overwrite;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }
}
