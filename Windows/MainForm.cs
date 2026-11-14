using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using PfsExplorerLite.Models;
using PfsExplorerLite.Services;
using PfsExplorerLite.Services.PfsNet;

namespace PfsExplorerLite;

internal sealed class MainForm : Form
{
    private const uint WmDropFiles = 0x0233;
    private const uint WmCopyData = 0x004A;
    private const uint WmCopyGlobalData = 0x0049;
    private const uint MsgfltAllow = 1;

    private readonly PfsShellClient _pfs;
    private readonly NbdClient _nbd;
    private readonly PfsNetClient _pfsNet = new();
    private readonly DiskDiscovery _diskDiscovery;
    private readonly ApaSpaceReader _apaSpaceReader;
    private readonly ApaPartitionTableReader _apaPartitionTableReader;
    private readonly PfsSpaceReader _pfsSpaceReader;
    private readonly ComboBox _deviceCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 549 };
    private readonly Button _refreshDisksButton = new() { Text = "刷新", AutoSize = true, Margin = new Padding(4) };
    private readonly TextBox _pathBox = new() { ReadOnly = true, Width = 510, Text = "/" };
    private readonly CheckBox _showSystemPartitionsCheckBox = new()
    {
        Text = "显示系统分区",
        AutoSize = true,
        Checked = false,
        Margin = new Padding(12, 8, 4, 0)
    };
    private readonly Label _diskSpaceLabel = new() { AutoSize = true, Padding = new Padding(8, 3, 0, 0), Text = "硬盘总计：—　未分配：—　当前分区：—　当前分区剩余：—" };
    private readonly Button _downloadArt2Button = new() { Text = "一键下载ART2图集", AutoSize = true, Margin = new Padding(4) };
    private readonly ListView _entries = new BrowserListView { View = View.Details, FullRowSelect = true, MultiSelect = true, LabelEdit = true, Dock = DockStyle.Fill };
    private readonly ContextMenuStrip _entryMenu = new();
    private readonly ToolStripMenuItem _copyMenuItem = new("复制");
    private readonly ToolStripMenuItem _deleteMenuItem = new("删除");
    private readonly ToolStripMenuItem _renameMenuItem = new("重命名");
    private readonly ToolStripMenuItem _pasteMenuItem = new("粘贴");
    private readonly ToolStripMenuItem _newDirectoryMenuItem = new("新建目录");
    private readonly ToolStripMenuItem _newPartitionMenuItem = new("新建 PFS 分区");
    private readonly ToolStripMenuItem _renamePartitionMenuItem = new("重命名分区");
    private readonly ToolStripMenuItem _deletePartitionMenuItem = new("删除 PFS 分区");
    private readonly ToolStripMenuItem _refreshMenuItem = new("刷新");
    private readonly ToolStripSeparator _entryMenuSeparator = new();
    private readonly ToolStripSeparator _entryMenuSeparator2 = new();
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9F) };
    private readonly StatusStrip _statusBar = new();
    private readonly ToolStripStatusLabel _statusLabel = new() { Text = "就绪", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripProgressBar _progressBar = new()
    {
        Visible = false,
        AutoSize = false,
        Width = 220,
        Margin = new Padding(2, 2, -70, 2),
        BackColor = Color.Gainsboro
    };
    private readonly ToolStripSpeedLabel _transferSpeedLabel = new() { Visible = false, AutoSize = false, Width = 240 };
    private readonly ToolStripButton _cancelTransferButton = new()
    {
        Text = "取消传输",
        Visible = false,
        Enabled = false,
        Alignment = ToolStripItemAlignment.Right
    };
    private readonly TextBox _pfsNetIpBox = new() { Width = 170, PlaceholderText = "PS2 PfsNet IP，例如 192.168.1.10" };
    private string _currentPath = "/";
    private string? _activePartition;
    private int _busyDepth;
    private int _fileTransferDepth;
    private Dictionary<Control, bool>? _busyControlStates;
    private bool _suppressShowSystemPartitionsEvent;
    private CancellationTokenSource? _transferCts;

    public MainForm()
    {
        var toolsDirectory = Path.Combine(AppContext.BaseDirectory, "tools");
        _pfs = new PfsShellClient(toolsDirectory);
        _nbd = new NbdClient(toolsDirectory);
        _diskDiscovery = new DiskDiscovery(toolsDirectory);
        _apaSpaceReader = new ApaSpaceReader();
        _apaPartitionTableReader = new ApaPartitionTableReader();
        _pfsSpaceReader = new PfsSpaceReader();

        Text = "PS2 HDD File Manager v1.0 — 峰哥原创，免费发布";
        MinimumSize = new Size(960, 660);
        Size = new Size(1008, 901);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        // 提权进程需在创建窗口前放行拖放消息，否则资源管理器拖入会被 UIPI 拦截。
        AllowDragDropFromLowerIntegrityProcessWide();
        BuildUi();
        // Backspace 在窗体级处理，避免焦点不在 ListView 时无法返回上级。
        KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Escape && _transferCts is not null)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                RequestCancelTransfer();
                return;
            }

            if (e.KeyCode != Keys.Back || IsEditableTextInputFocused())
                return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            await GoUpAsync();
        };
        Shown += async (_, _) => await RefreshDisksAsync();
        FormClosed += (_, _) =>
        {
            try { _pfsNet.Disconnect(); } catch { }
            _pfsNet.Dispose();
            _nbd.Dispose();
        };
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
            // 只接受 BeginRename（单击延时 / 文件双击 / F2 / 菜单），屏蔽系统原生误触发以免闪一下又取消。
            var browser = (BrowserListView)_entries;
            if (!browser.AllowNextLabelEdit
                || _activePartition is null
                || _entries.Items[e.Item].Tag is not PfsEntry)
            {
                e.CancelEdit = true;
                return;
            }
            browser.AllowNextLabelEdit = false;
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
        _entries.ItemDrag += async (_, e) =>
        {
            if (e.Button != MouseButtons.Left || _activePartition is null || SelectedEntries().Count == 0)
                return;
            await DragSelectedToWindowsAsync();
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
            // 主键盘 Delete 与小键盘 Del（NumLock 关闭时）均为 Keys.Delete。
            if (e.KeyCode == Keys.Delete)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                if (_activePartition is null)
                    await DeleteSelectedPartitionAsync();
                else
                    await DeleteSelectedAsync();
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
        _newPartitionMenuItem.Click += async (_, _) => await CreatePfsPartitionAsync();
        _renamePartitionMenuItem.Click += async (_, _) => await RenameSelectedPartitionAsync();
        _deletePartitionMenuItem.Click += async (_, _) => await DeleteSelectedPartitionAsync();
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
            _newPartitionMenuItem,
            _renamePartitionMenuItem,
            _deletePartitionMenuItem,
            _refreshMenuItem
        ]);
        _entryMenu.Opening += (_, e) =>
        {
            var selected = SelectedEntries();
            var inPartition = _activePartition is not null;
            var onFileItem = inPartition && selected.Count > 0;
            _copyMenuItem.Visible = onFileItem;
            _deleteMenuItem.Visible = onFileItem;
            _renameMenuItem.Visible = onFileItem;
            _entryMenuSeparator.Visible = onFileItem;
            _copyMenuItem.Enabled = onFileItem;
            _deleteMenuItem.Enabled = onFileItem;
            _renameMenuItem.Enabled = selected.Count == 1;
            _pasteMenuItem.Visible = inPartition;
            _newDirectoryMenuItem.Visible = inPartition;
            _pasteMenuItem.Enabled = inPartition && ClipboardHasImportableItems();
            _newDirectoryMenuItem.Enabled = inPartition;
            _entryMenuSeparator2.Visible = inPartition;
            _newPartitionMenuItem.Visible = !inPartition;
            _newPartitionMenuItem.Enabled = !inPartition && _deviceCombo.SelectedItem is DeviceItem;
            _renamePartitionMenuItem.Visible = !inPartition;
            _renamePartitionMenuItem.Enabled = !inPartition && selected.Count == 1 && !IsProtectedPartitionName(selected[0].Name) && _deviceCombo.SelectedItem is DeviceItem;
            _deletePartitionMenuItem.Visible = !inPartition;
            _deletePartitionMenuItem.Enabled = !inPartition && selected.Count == 1 && !IsProtectedPartitionName(selected[0].Name) && _deviceCombo.SelectedItem is DeviceItem;
            e.Cancel = false;
        };
        _entries.ContextMenuStrip = _entryMenu;
        _deviceCombo.SelectionChangeCommitted += async (_, _) => await LoadPartitionsAsync();
        _refreshDisksButton.Click += async (_, _) => await RefreshDisksAsync();

        var sourceBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        sourceBar.Controls.AddRange(
        [
            new Label { Text = "来源：", AutoSize = true, Padding = new Padding(0, 8, 0, 0) },
            _deviceCombo,
            _refreshDisksButton,
            MakeButton("加载硬盘镜像", SelectImage)
        ]);

        var ejectBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        ejectBar.Controls.Add(MakeButton("安全弹出硬盘", async (_, _) => await SafeEjectSelectedAsync()));

        var art2Bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        art2Bar.Controls.Add(_downloadArt2Button);
        _downloadArt2Button.Click += async (_, _) => await DownloadArt2Async();

        // NBD UI 暂时隐藏，代码保留待后续开发
        var nbdBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Visible = false };
        var ipBox = new TextBox { Width = 170, PlaceholderText = "PS2 NBD IP，例如 192.168.1.10" };
        nbdBar.Controls.AddRange(
        [
            new Label { Text = "网络(NBD)：", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, ipBox,
            MakeButton("连接 NBD", async (_, _) => await ConnectNbdAsync(ipBox.Text)),
            MakeButton("断开 NBD", async (_, _) => await DisconnectNbdAsync())
        ]);

        // PfsNet：对接 PS2 端 ps2-server ELF（与 NBD 互斥，见 PROTOCOL.md）。
        var pfsNetBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        var lastPfsNetIp = LoadLastPfsNetIp();
        if (!string.IsNullOrWhiteSpace(lastPfsNetIp))
            _pfsNetIpBox.Text = lastPfsNetIp;
        pfsNetBar.Controls.AddRange(
        [
            new Label { Text = "PFS 网络：", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, _pfsNetIpBox,
            MakeButton("连接 PFS 网络", async (_, _) => await ConnectPfsNetAsync(_pfsNetIpBox.Text)),
            MakeButton("断开 PFS 网络", async (_, _) => await DisconnectPfsNetAsync())
        ]);

        var actionBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        actionBar.Controls.AddRange(
        [
            MakeButton("上级目录", async (_, _) => await GoUpAsync()),
            _showSystemPartitionsCheckBox,
            _pathBox
        ]);
        _showSystemPartitionsCheckBox.CheckedChanged += async (_, _) => await OnShowSystemPartitionsChangedAsync();

        var diskSpaceBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        _diskSpaceLabel.Margin = new Padding(0, 4, 0, 0);
        diskSpaceBar.Controls.Add(_diskSpaceLabel);

        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(10) };
        top.Controls.Add(sourceBar);
        top.Controls.Add(nbdBar);
        top.Controls.Add(pfsNetBar);
        top.Controls.Add(ejectBar);
        top.Controls.Add(art2Bar);
        top.Controls.Add(diskSpaceBar);
        top.Controls.Add(actionBar);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var browserPage = new TabPage("PFS 浏览器");
        browserPage.Controls.Add(_entries);
        var logPage = new TabPage("诊断输出");
        logPage.Controls.Add(_log);
        tabs.TabPages.Add(browserPage);
        tabs.TabPages.Add(logPage);
        _cancelTransferButton.Click += (_, _) => RequestCancelTransfer();
        _statusBar.Items.AddRange([_statusLabel, _cancelTransferButton, _progressBar, _transferSpeedLabel]);
        _transferSpeedLabel.BringControlToFront();
        Controls.Add(tabs);
        Controls.Add(top);
        Controls.Add(_statusBar);
    }

    private Button MakeButton(string text, EventHandler click) => new Button { Text = text, AutoSize = true, Margin = new Padding(4) }.Also(button => button.Click += click);

    private Task DownloadArt2Async()
    {
        // 预留：之后将按游戏 ID 在线下载图集，并写入当前 PFS 分区下的 ART2/ 目录。
        AppendLog("「一键下载ART2图集」尚未实现：后续将下载到当前分区的 ART2 文件夹。");
        ShowWarning("该功能尚未实现。\n\n预留说明：图集将下载到当前 PFS 分区内的 ART2 文件夹。");
        return Task.CompletedTask;
    }

    private async Task RefreshDisksAsync()
    {
        await RunBusyAsync("正在扫描 PS2 APA 硬盘…", async () =>
        {
            var disks = await _diskDiscovery.GetPs2ApaDisksAsync(CancellationToken.None);
            _deviceCombo.Items.Clear();
            ResetPfsExplorer();
            foreach (var disk in disks)
                AddSourceDevice(disk.DevicePath, disk.DisplayName);
            // 刷新物理盘列表时保留已连接的 PfsNet 来源。
            if (_pfsNet.IsConnected && _pfsNet.Host is { } host)
                AddSourceDevice(PfsNetProtocol.MakeDevicePath(host), $"PS2 PfsNet · {host}");
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

    private async Task EnsureNetworkUnmountedForApaAsync()
    {
        if (_pfsNet.MountedPartition is null)
            return;
        await _pfsNet.UmountAsync(CancellationToken.None);
        _activePartition = null;
        _currentPath = "/";
    }

    private async Task<ApaPartitionTable> ReadApaLayoutAsync(string device, CancellationToken cancellationToken)
    {
        if (!PfsNetProtocol.IsNetworkDevice(device))
            return await _apaPartitionTableReader.ReadAsync(device, cancellationToken);

        if (!_pfsNet.SupportsApaMutate)
            throw new InvalidOperationException("当前 PfsNet 未启用 APA 扇区校验/分区变更。");
        await EnsureNetworkUnmountedForApaAsync();
        var space = await _pfsNet.GetApaFreeAsync(cancellationToken);
        if (space.TotalBytes == 0)
            throw new InvalidOperationException("GET_APA_FREE 返回总容量为 0，无法校验分区表。");
        var source = new PfsNetApaSectorSource(_pfsNet, checked((long)space.TotalBytes));
        return await _apaPartitionTableReader.ReadAsync(source, cancellationToken);
    }

    private async Task CreatePfsPartitionAsync()
    {
        if (!TryGetDevice(out var device)) return;
        if (RejectNetworkPartitionMutationIfNeeded(device)) return;

        var isNet = PfsNetProtocol.IsNetworkDevice(device);
        ApaPartitionTable layout;
        try
        {
            layout = await ReadApaLayoutAsync(device, CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppendLog($"拒绝创建分区：无法通过 APA 校验读取分区表。{ex.Message}");
            ShowWarning("分区表无法通过完整校验，已拒绝写入。请先备份硬盘并修复分区表。");
            return;
        }

        var request = PartitionCreatePrompt.Show(this, FormatBytes(layout.FreeBytes), FormatPartitionSize(layout.FreeBytes));
        if (request is null) return;
        if (!TryParsePartitionSize(request.Size, out var requestedBytes, out var normalizedSize, out var sizeError))
        {
            ShowWarning(sizeError);
            return;
        }
        if (!IsSafePartitionName(request.Name, out var nameError))
        {
            ShowWarning(nameError);
            return;
        }
        if (layout.Partitions.Any(partition => !partition.IsSubPartition && partition.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase)))
        {
            ShowWarning($"分区“{request.Name}”已存在。");
            return;
        }
        if (requestedBytes > layout.FreeBytes)
        {
            ShowWarning($"APA 未分配空间不足。请求 {FormatBytes(requestedBytes)}，当前只有 {FormatBytes(layout.FreeBytes)}。未执行任何写入。");
            return;
        }

        var sourceDisplay = (_deviceCombo.SelectedItem as DeviceItem)?.Display ?? device;
        if (!PartitionCreateConfirmation.Show(this, sourceDisplay, request.Name, FormatBytes(requestedBytes)))
            return;

        var created = false;
        await RunBusyAsync($"正在创建 PFS 分区“{request.Name}” ({normalizedSize})…", async () =>
        {
            // 用户打开确认框期间来源可能已改变：再次读取、校验并预检，避免在过期布局上写入。
            var currentLayout = await ReadApaLayoutAsync(device, CancellationToken.None);
            if (currentLayout.Partitions.Any(partition => !partition.IsSubPartition && partition.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"分区“{request.Name}”已存在，已取消创建。");
            if (requestedBytes > currentLayout.FreeBytes)
                throw new InvalidOperationException("APA 未分配空间已变化且不足，已取消创建。");

            if (isNet)
                await _pfsNet.ApaCreatePfsAsync(request.Name, normalizedSize, CancellationToken.None);
            else
            {
                // pfsshell/PS2SDK 负责依照当前硬盘支持的单分区上限，自动创建主分区及所需子分区。
                var create = await _pfs.CreatePfsPartitionAsync(device, request.Name, normalizedSize, CancellationToken.None);
                AppendResult(create);
                if (!PfsShellClient.IsSuccessfulSession(create))
                    throw new InvalidOperationException("pfsshell 未能创建 PFS 分区，请查看诊断输出。");
            }

            var afterCreate = await ReadApaLayoutAsync(device, CancellationToken.None);
            var createdPartition = afterCreate.Partitions.SingleOrDefault(partition =>
                !partition.IsSubPartition && partition.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase));
            var hasExactSize = createdPartition is not null && createdPartition.Type == 0x0100 && createdPartition.TotalBytes == requestedBytes;

            // pfsshell 在空间碎片或不足时可能保留一个较小但有效的分区；绝不能把它当作成功。
            if (!hasExactSize)
            {
                var actualSize = createdPartition is null ? "未找到" : FormatBytes(createdPartition.TotalBytes);
                AppendLog($"创建后核验失败：请求 {FormatBytes(requestedBytes)}，实际 {actualSize}。将尝试删除不完整分区。");
                if (isNet)
                    await _pfsNet.ApaRemoveAsync(request.Name, CancellationToken.None);
                else
                {
                    var rollback = await _pfs.RemovePartitionAsync(device, request.Name, CancellationToken.None);
                    AppendResult(rollback);
                    if (!PfsShellClient.IsSuccessfulSession(rollback))
                        throw new InvalidOperationException($"新分区容量核验失败，且自动清理失败。请立即停止写入并备份硬盘；诊断中的分区名为“{request.Name}”。");
                }
                throw new InvalidOperationException($"空间布局无法完整创建 {FormatBytes(requestedBytes)} 的分区；已自动删除未完成分区，未保留任何新分区。");
            }

            if (isNet)
            {
                await _pfsNet.MountAsync(request.Name, CancellationToken.None);
                try
                {
                    _ = await _pfsNet.ListDirAsync("/", CancellationToken.None);
                }
                catch
                {
                    try { await _pfsNet.UmountAsync(CancellationToken.None); } catch { }
                    AppendLog("PFS 格式化后无法挂载，将尝试删除新分区。");
                    await _pfsNet.ApaRemoveAsync(request.Name, CancellationToken.None);
                    throw new InvalidOperationException("新分区无法通过 PFS 挂载验证，已自动删除。");
                }
                await _pfsNet.UmountAsync(CancellationToken.None);
            }
            else
            {
                var verifyMount = await _pfs.ListDirectoryAsync(device, request.Name, "/", CancellationToken.None);
                AppendResult(verifyMount);
                if (!PfsShellClient.IsSuccessfulSession(verifyMount))
                {
                    AppendLog("PFS 格式化后无法挂载，将尝试删除新分区。");
                    var rollback = await _pfs.RemovePartitionAsync(device, request.Name, CancellationToken.None);
                    AppendResult(rollback);
                    if (!PfsShellClient.IsSuccessfulSession(rollback))
                        throw new InvalidOperationException($"新分区无法验证挂载且自动清理失败。请立即停止写入并备份硬盘；诊断中的分区名为“{request.Name}”。");
                    throw new InvalidOperationException("新分区无法通过 PFS 挂载验证，已自动删除。");
                }
            }

            created = true;
            AppendLog($"已创建并验证 PFS 分区“{request.Name}”：{FormatBytes(requestedBytes)}。子分区由 PS2SDK 自动管理。");
        });

        if (created)
            await LoadPartitionsAsync();
    }

    private async Task DeleteSelectedPartitionAsync()
    {
        if (_activePartition is not null)
            return;
        if (!TryGetDevice(out var device))
            return;
        if (RejectNetworkPartitionMutationIfNeeded(device))
            return;
        if (SelectedEntry() is not { } selected)
        {
            ShowWarning("请在分区列表中选择一个要删除的 PFS 分区。");
            return;
        }

        var name = selected.Name;
        if (IsProtectedPartitionName(name))
        {
            AppendLog($"拒绝删除系统保留分区“{name}”。");
            ShowWarning($"“{name}”是系统保留分区，程序禁止删除。");
            return;
        }

        var isNet = PfsNetProtocol.IsNetworkDevice(device);
        ApaPartitionTable layout;
        ApaPartitionInfo? target;
        try
        {
            layout = await ReadApaLayoutAsync(device, CancellationToken.None);
            target = layout.Partitions.SingleOrDefault(partition =>
                !partition.IsSubPartition && partition.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            AppendLog($"拒绝删除分区：无法通过 APA 校验读取分区表。{ex.Message}");
            ShowWarning("分区表无法通过完整校验，已拒绝删除。请先备份硬盘并修复分区表。");
            return;
        }

        if (target is null || target.Type != 0x0100)
        {
            AppendLog($"拒绝删除分区“{name}”：目标不是有效的 PFS 主分区。");
            ShowWarning("目标不是可安全删除的 PFS 主分区，已拒绝操作。");
            return;
        }

        var sourceDisplay = (_deviceCombo.SelectedItem as DeviceItem)?.Display ?? device;
        var size = FormatBytes(target.TotalBytes);
        if (MessageBox.Show(
                this,
                $"将永久删除 PFS 分区“{name}”（{size}）及其全部文件。\n\n来源：{sourceDisplay}\n\n此操作会修改 APA 分区表，删除后无法恢复。确定继续？",
                "第一次确认：删除 PFS 分区",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        var secondConfirm = isNet
            ? $"最后确认：立即删除“{name}”？\n\n程序将通过 PfsNet 删除分区，并在删除后重新校验 APA 分区表。"
            : $"最后确认：立即删除“{name}”？\n\n程序将使用 pfsshell 的标准 rmpart 操作，并在删除后重新校验 APA 分区表。";
        if (MessageBox.Show(
                this,
                secondConfirm,
                "第二次确认：永久删除",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        var deleted = false;
        await RunBusyAsync($"正在删除 PFS 分区“{name}”…", async () =>
        {
            // 两次确认期间不信任旧状态：再次完整校验，防止目标已变化或分区表异常。
            var beforeDelete = await ReadApaLayoutAsync(device, CancellationToken.None);
            var currentTarget = beforeDelete.Partitions.SingleOrDefault(partition =>
                !partition.IsSubPartition && partition.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (currentTarget is null || currentTarget.Type != 0x0100 || currentTarget.TotalBytes != target.TotalBytes)
                throw new InvalidOperationException("删除前分区表状态已变化，已取消删除。");
            if (IsProtectedPartitionName(currentTarget.Name))
                throw new InvalidOperationException("目标属于系统保留分区，已取消删除。");

            if (isNet)
                await _pfsNet.ApaRemoveAsync(name, CancellationToken.None);
            else
            {
                var result = await _pfs.RemovePartitionAsync(device, name, CancellationToken.None);
                AppendResult(result);

                // 此版本 pfsshell 的 rmpart 在成功后仍会打印 "No such file" / exit -2。
                // 因此不能以工具文本作为结论，必须以删除后可通过校验的 APA 表为准。
                var afterDeleteLocal = await ReadApaLayoutAsync(device, CancellationToken.None);
                var stillExistsLocal = afterDeleteLocal.Partitions.Any(partition =>
                    !partition.IsSubPartition && partition.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (stillExistsLocal || afterDeleteLocal.FreeBytes <= beforeDelete.FreeBytes)
                {
                    var toolState = PfsShellClient.IsSuccessfulSession(result) ? "" : "pfsshell 同时报告了错误。";
                    throw new InvalidOperationException($"删除后的 APA 分区表校验未通过，目标分区仍存在或未分配空间未增加。{toolState}请立即停止写入、保留硬盘现状并查看诊断输出。");
                }

                deleted = true;
                if (!PfsShellClient.IsSuccessfulSession(result))
                    AppendLog("pfsshell 的 rmpart 返回了旧版误报，但 APA 分区表复核确认删除已成功。");
                AppendLog($"已删除并重新校验 PFS 分区“{name}”（{size}）。APA 未分配空间已增加。");
                return;
            }

            var afterDelete = await ReadApaLayoutAsync(device, CancellationToken.None);
            var stillExists = afterDelete.Partitions.Any(partition =>
                !partition.IsSubPartition && partition.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (stillExists || afterDelete.FreeBytes <= beforeDelete.FreeBytes)
                throw new InvalidOperationException("删除后的 APA 分区表校验未通过，目标分区仍存在或未分配空间未增加。请立即停止写入、保留硬盘现状并查看诊断输出。");

            deleted = true;
            AppendLog($"已删除并重新校验 PFS 分区“{name}”（{size}）。APA 未分配空间已增加。");
        });

        if (deleted)
            await LoadPartitionsAsync();
    }

    private async Task RenameSelectedPartitionAsync()
    {
        if (_activePartition is not null || !TryGetDevice(out var device))
            return;
        if (RejectNetworkPartitionMutationIfNeeded(device))
            return;
        if (SelectedEntry() is not { } selected)
        {
            ShowWarning("请在分区列表中选择一个要重命名的 PFS 分区。");
            return;
        }

        var oldName = selected.Name;
        if (IsProtectedPartitionName(oldName))
        {
            ShowWarning($"“{oldName}”是系统保留分区，程序禁止重命名。");
            return;
        }

        var newName = Prompt.Show(this, "重命名 PFS 分区", "新分区名称：", oldName);
        if (newName is null || newName.Equals(oldName, StringComparison.Ordinal))
            return;
        if (!IsSafePartitionName(newName, out var nameError))
        {
            ShowWarning(nameError);
            return;
        }
        if (IsProtectedPartitionName(newName))
        {
            ShowWarning("不能使用系统保留分区名称。");
            return;
        }

        ApaPartitionInfo? target;
        var isNet = PfsNetProtocol.IsNetworkDevice(device);
        try
        {
            var layout = await ReadApaLayoutAsync(device, CancellationToken.None);
            target = layout.Partitions.SingleOrDefault(partition =>
                !partition.IsSubPartition && partition.Name.Equals(oldName, StringComparison.OrdinalIgnoreCase));
            if (target is null || target.Type != 0x0100)
            {
                ShowWarning("目标不是有效的 PFS 主分区，已取消重命名。");
                return;
            }
            if (layout.Partitions.Any(partition => !partition.IsSubPartition && partition.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
            {
                ShowWarning($"分区“{newName}”已存在。");
                return;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"拒绝重命名分区：无法通过 APA 校验读取分区表。{ex.Message}");
            ShowWarning("分区表无法通过完整校验，已拒绝重命名。请先备份硬盘并修复分区表。");
            return;
        }

        var sourceDisplay = (_deviceCombo.SelectedItem as DeviceItem)?.Display ?? device;
        if (MessageBox.Show(
                this,
                $"将把 PFS 分区“{oldName}”重命名为“{newName}”。\n\n来源：{sourceDisplay}\n\n此操作只修改 APA 分区表中的名称，不移动分区或文件数据。请勿断电、拔出硬盘或强制关闭程序。确定继续？",
                "确认重命名 PFS 分区",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        var renamed = false;
        await RunBusyAsync($"正在重命名 PFS 分区“{oldName}”…", async () =>
        {
            var beforeRename = await ReadApaLayoutAsync(device, CancellationToken.None);
            var currentTarget = beforeRename.Partitions.SingleOrDefault(partition =>
                !partition.IsSubPartition && partition.Name.Equals(oldName, StringComparison.OrdinalIgnoreCase));
            if (currentTarget is null || currentTarget.Type != 0x0100 || !SamePartitionLayout(currentTarget, target!))
                throw new InvalidOperationException("重命名前分区表状态已变化，已取消操作。");
            if (IsProtectedPartitionName(currentTarget.Name) || beforeRename.Partitions.Any(partition =>
                    !partition.IsSubPartition && partition.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("重命名前发现系统保留名称或同名分区，已取消操作。");

            if (isNet)
                await _pfsNet.ApaRenameAsync(oldName, newName, CancellationToken.None);
            else
            {
                var result = await _pfs.RenamePartitionAsync(device, oldName, newName, CancellationToken.None);
                AppendResult(result);
                if (!PfsShellClient.IsSuccessfulSession(result))
                    throw new InvalidOperationException("pfsshell 未能重命名 PFS 分区，请查看诊断输出。");
            }

            var afterRename = await ReadApaLayoutAsync(device, CancellationToken.None);
            var renamedTarget = afterRename.Partitions.SingleOrDefault(partition =>
                !partition.IsSubPartition && partition.Name.Equals(newName, StringComparison.OrdinalIgnoreCase));
            var oldTarget = afterRename.Partitions.Any(partition =>
                !partition.IsSubPartition && partition.Name.Equals(oldName, StringComparison.OrdinalIgnoreCase));
            if (renamedTarget is null || oldTarget || !SamePartitionLayout(renamedTarget, target!))
                throw new InvalidOperationException("重命名后的 APA 分区表校验未通过。请立即停止写入、保留硬盘现状并查看诊断输出。");

            renamed = true;
            AppendLog($"已重命名 PFS 分区“{oldName}”为“{newName}”，分区位置、容量和子分区布局已复核一致。");
        });

        if (renamed)
            await LoadPartitionsAsync();
    }

    private static bool SamePartitionLayout(ApaPartitionInfo left, ApaPartitionInfo right) =>
        left.StartSector == right.StartSector &&
        left.LengthSectors == right.LengthSectors &&
        left.Type == right.Type &&
        left.Flags == right.Flags &&
        left.MainSector == right.MainSector &&
        left.Number == right.Number &&
        left.SubPartitions.SequenceEqual(right.SubPartitions);

    private static bool IsProtectedPartitionName(string name) =>
        name.Equals("__mbr", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("__net", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("__system", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("__sysconf", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("__common", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("__boot", StringComparison.OrdinalIgnoreCase);

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
        _diskSpaceLabel.Text = "硬盘总计：—　未分配：—　当前分区：—　当前分区剩余：—";
    }

    private async Task ConnectNbdAsync(string ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress)) { ShowWarning("请输入 PS2 的 NBD IP 地址。"); return; }
        if (_pfsNet.IsConnected)
        {
            ShowWarning("已连接 PFS 网络。请先断开 PFS 网络后再连接 NBD（二者互斥）。");
            return;
        }

        string? mappedDevicePath = null;
        await RunBusyAsync("正在连接 NBD…", async () =>
        {
            var result = await _nbd.MapAsync(ipAddress.Trim(), CancellationToken.None);
            AppendResult(result);
            if (result.ExitCode != 0) throw new InvalidOperationException("WNBD 映射失败，请查看诊断输出。");

            // 不再调用 hdl_dump query：该命令会探测所有 Windows 物理磁盘，可能拖慢或卡住 PS2 NBD Server。
            var diskNumber = await _nbd.GetMappedDiskNumberAsync(CancellationToken.None);
            mappedDevicePath = $@"\\.\PhysicalDrive{diskNumber}";
            AddSourceDevice(mappedDevicePath, $"PS2 NBD · {ipAddress.Trim()} · PhysicalDrive{diskNumber}");
            _deviceCombo.SelectedIndex = _deviceCombo.Items.Count - 1;
            ResetPfsExplorer();
            AppendLog($"NBD 已映射为 PhysicalDrive{diskNumber}，将只读取该硬盘的 PFS 分区。");
        });

        if (mappedDevicePath is not null)
            await LoadPartitionsAsync();
    }

    private async Task DisconnectNbdAsync() => await RunBusyAsync("正在断开 NBD…", async () =>
    {
        var result = await _nbd.UnmapAsync(CancellationToken.None);
        AppendResult(result);
        if (result.ExitCode != 0) throw new InvalidOperationException("WNBD 断开失败，请查看诊断输出。");
        await RefreshDisksAsync();
    });

    private async Task ConnectPfsNetAsync(string ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            ShowWarning("请输入运行 ps2-server 的 PS2 IP 地址。");
            return;
        }

        if (_nbd.IsMapRunning)
        {
            ShowWarning("NBD 映射仍在运行。请先断开 NBD 后再连接 PFS 网络（二者互斥）。");
            return;
        }

        SaveLastPfsNetIp(ipAddress.Trim());
        await RunBusyAsync("正在连接 PfsNet…", async () =>
        {
            await _pfsNet.ConnectAsync(ipAddress.Trim(), CancellationToken.None);
            var path = PfsNetProtocol.MakeDevicePath(ipAddress.Trim());
            AddSourceDevice(path, $"PS2 PfsNet · {ipAddress.Trim()}");
            _deviceCombo.SelectedIndex = _deviceCombo.Items.Count - 1;
            ResetPfsExplorer();
            AppendLog($"已连接 PfsNet（{ipAddress.Trim()}:{PfsNetProtocol.CommandPort}）。数据口：本机 TCP {PfsNetProtocol.DataPort}。features=0x{_pfsNet.Features:X}。");
            if ((_pfsNet.Features & PfsNetProtocol.FeatStreamRing) == 0)
            {
                AppendLog("提示：检测到 PS2 端 ApaNetServer.ELF 为旧版（未开启 8MB 环形缓冲与高速线程调度），请同步替换 PS2 上的 ApaNetServer.ELF 以获得完整传输速度与防断线优化。");
            }
        });

        if (_pfsNet.IsConnected)
            await LoadPartitionsAsync();
    }

    private async Task DisconnectPfsNetAsync()
    {
        await RunBusyAsync("正在断开 PfsNet…", () =>
        {
            // 断开时只做本地清理：对端已掉线时远程 umount 读写会卡死；PS2 断线会自行 force umount
            var host = _pfsNet.Host;
            _pfsNet.Disconnect();
            if (host is not null)
            {
                var path = PfsNetProtocol.MakeDevicePath(host);
                for (var i = _deviceCombo.Items.Count - 1; i >= 0; i--)
                {
                    if (_deviceCombo.Items[i] is DeviceItem item && item.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
                        _deviceCombo.Items.RemoveAt(i);
                }
            }

            ResetPfsExplorer();
            AppendLog("已断开 PfsNet。");
            return Task.CompletedTask;
        });

        if (_deviceCombo.Items.Count == 0)
        {
            _deviceCombo.Items.Add(new DeviceItem(string.Empty, "未检测到PS2硬盘"));
            _deviceCombo.SelectedIndex = 0;
        }
        else if (_deviceCombo.SelectedItem is null)
        {
            _deviceCombo.SelectedIndex = 0;
            if ((_deviceCombo.SelectedItem as DeviceItem)?.Path is { Length: > 0 })
                await LoadPartitionsAsync();
        }
    }

    private bool RejectNetworkPartitionMutationIfNeeded(string device)
    {
        if (!PfsNetProtocol.IsNetworkDevice(device))
            return false;
        if (_pfsNet.SupportsApaMutate)
            return false;
        ShowWarning("网络模式分区管理尚未启用。\n\n需待 APA 扇区校验（A1）与分区变更命令就绪后可用。当前仅支持文件/目录浏览与后续文件操作。");
        return true;
    }

    private async Task SafeEjectSelectedAsync()
    {
        if (Volatile.Read(ref _fileTransferDepth) != 0)
        {
            ShowWarning("正在传输文件，请等待完成后再安全弹出。");
            return;
        }

        if (_deviceCombo.SelectedItem is not DeviceItem item || string.IsNullOrWhiteSpace(item.Path))
        {
            ShowWarning("请先选择要弹出的 PS2 硬盘。");
            return;
        }

        if (PfsNetProtocol.IsNetworkDevice(item.Path))
        {
            if (MessageBox.Show(this,
                    "当前来源是 PFS 网络连接，无法硬件弹出。\n是否断开 PfsNet？",
                    Text,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            await DisconnectPfsNetAsync();
            return;
        }

        // 镜像文件不是可弹出设备，仅从列表移除。
        if (!DiskSafeEject.TryParsePhysicalDriveNumber(item.Path, out var driveNumber))
        {
            if (MessageBox.Show(this,
                    $"“{item.Display}”是镜像文件，无法执行硬件弹出。\n是否仅从列表中移除？",
                    Text,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            RemoveSelectedSourceDevice();
            ResetPfsExplorer();
            AppendLog($"已从列表移除镜像：{item.Path}");
            return;
        }

        var isNbd = item.Display.StartsWith("PS2 NBD", StringComparison.OrdinalIgnoreCase);
        var action = isNbd ? "断开 NBD 映射" : "安全弹出";
        if (MessageBox.Show(this,
                $"确定要{action}吗？\n\n{item.Display}\n\n弹出前请确认没有其它程序正在访问该硬盘。",
                Text,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        if (isNbd)
        {
            await DisconnectNbdAsync();
            return;
        }

        var ejected = false;
        var outcome = DiskEjectOutcome.Ejected;
        await RunBusyAsync($"正在安全弹出 PhysicalDrive{driveNumber}…", async () =>
        {
            outcome = await Task.Run(() => DiskSafeEject.EjectPhysicalDrive(driveNumber));
            ejected = true;
            AppendLog(outcome == DiskEjectOutcome.FlushedNonHotPlug
                ? DiskSafeEject.NonHotPlugReadyMessage(driveNumber)
                : $"已请求安全弹出 PhysicalDrive{driveNumber}。");
        });
        if (!ejected)
            return;

        RemoveSelectedSourceDevice();
        ResetPfsExplorer();
        if (outcome == DiskEjectOutcome.FlushedNonHotPlug)
        {
            ShowInfo(DiskSafeEject.NonHotPlugReadyMessage(driveNumber) + "\n\n硬盘已从列表移除。");
            return;
        }

        AppendLog("硬盘已从列表移除。若托盘仍提示可安全移除，请再确认硬件指示灯熄灭后拔出。");
    }

    private void RemoveSelectedSourceDevice()
    {
        var index = _deviceCombo.SelectedIndex;
        if (index < 0)
            return;
        _deviceCombo.Items.RemoveAt(index);
        if (_deviceCombo.Items.Count == 0)
        {
            _deviceCombo.Items.Add(new DeviceItem(string.Empty, "未检测到PS2硬盘"));
            _deviceCombo.SelectedIndex = 0;
            return;
        }

        _deviceCombo.SelectedIndex = Math.Min(index, _deviceCombo.Items.Count - 1);
    }
    private async Task LoadPartitionsAsync()
    {
        if (!TryGetDevice(out var device)) return;
        await RunBusyAsync("正在读取分区表…", async () =>
        {
            IReadOnlyList<string> partitions;
            Dictionary<string, ulong>? netSizes = null;
            if (PfsNetProtocol.IsNetworkDevice(device))
            {
                try { await _pfsNet.UmountAsync(CancellationToken.None); } catch { }
                var infos = await _pfsNet.ListPartitionsAsync(
                    _showSystemPartitionsCheckBox.Checked,
                    CancellationToken.None);
                partitions = infos
                    .Where(p => p.IsPfs)
                    .Select(p => p.Name)
                    .Where(name => _showSystemPartitionsCheckBox.Checked || !IsProtectedPartitionName(name))
                    .ToList();
                netSizes = infos
                    .Where(p => p.IsPfs)
                    .ToDictionary(p => p.Name, p => p.TotalBytes, StringComparer.OrdinalIgnoreCase);
                AppendLog($"PfsNet 分区列表：{partitions.Count} 个可浏览项。");
            }
            else
            {
                var result = await _pfs.ListPartitionsAsync(device, CancellationToken.None);
                AppendResult(result);
                var apaPartitions = PfsShellClient.ParsePartitionNames(result.StandardOutput);
                partitions = await _pfs.FilterBrowsablePfsPartitionsAsync(device, apaPartitions, CancellationToken.None);
                if (!_showSystemPartitionsCheckBox.Checked)
                    partitions = partitions.Where(name => !IsProtectedPartitionName(name)).ToList();
            }

            _activePartition = null;
            await UpdateDiskSpaceAsync(device, null);
            _currentPath = "/";
            _entries.BeginUpdate();
            _entries.Items.Clear();
            foreach (var partition in partitions)
            {
                var isSystem = IsProtectedPartitionName(partition);
                var sizeHint = "";
                if (netSizes is not null
                    && netSizes.TryGetValue(partition, out var totalBytes)
                    && totalBytes > 0)
                {
                    sizeHint = $" · {FormatCapacity((long)Math.Min(totalBytes, long.MaxValue))}";
                }

                var item = new ListViewItem(partition)
                {
                    Tag = new PfsEntry(partition, true, isSystem ? "系统保留分区；可浏览读写，禁止删除/重命名分区" : "可操作的 PFS 分区；双击查看")
                };
                item.SubItems.Add(isSystem ? $"系统分区{sizeHint}" : $"PFS 分区{sizeHint}");
                item.SubItems.Add(isSystem
                    ? "系统保留分区；可浏览读写，禁止删除/重命名分区"
                    : "可操作的 PFS 分区；双击查看");
                _entries.Items.Add(item);
            }
            _entries.EndUpdate();
            _pathBox.Text = "PFS 分区（双击查看）";
            AppendLog(partitions.Count > 0
                ? $"识别到 {partitions.Count} 个可浏览 PFS 分区。双击分区开始浏览。"
                : "未找到可浏览的 PFS 分区。请查看诊断输出。");
        });
    }

    private async Task OnShowSystemPartitionsChangedAsync()
    {
        if (_suppressShowSystemPartitionsEvent)
            return;

        if (_showSystemPartitionsCheckBox.Checked)
        {
            var confirmed = MessageBox.Show(
                this,
                "即将显示系统保留分区（如 __system、__common 等）。\n\n" +
                "确认后可以浏览并自由读写这些分区内的文件，但禁止删除或重命名系统分区本身。\n" +
                "误操作可能导致 PS2 无法正常启动。\n\n是否继续？",
                Text,
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning) == DialogResult.OK;
            if (!confirmed)
            {
                _suppressShowSystemPartitionsEvent = true;
                _showSystemPartitionsCheckBox.Checked = false;
                _suppressShowSystemPartitionsEvent = false;
                return;
            }

            AppendLog("已启用显示系统分区：可读写分区内文件，仍禁止删除/重命名系统分区。");
        }
        else
        {
            AppendLog("已隐藏系统分区。");
            if (_activePartition is not null && IsProtectedPartitionName(_activePartition))
            {
                _activePartition = null;
                _currentPath = "/";
            }
        }

        // 仅在分区列表视图下刷新；若正在普通分区内浏览则无需重载目录。
        if (_activePartition is null && _deviceCombo.SelectedItem is DeviceItem { Path.Length: > 0 })
            await LoadPartitionsAsync();
    }

    private async Task RefreshDirectoryAsync()
    {
        if (!TryGetPfsTarget(out var device, out var partition)) return;
        await RunBusyAsync("正在读取目录…", async () =>
        {
            IReadOnlyList<PfsEntry> entries;
            if (PfsNetProtocol.IsNetworkDevice(device))
            {
                await _pfsNet.MountAsync(partition, CancellationToken.None);
                var remote = await _pfsNet.ListDirAsync(_currentPath, CancellationToken.None);
                entries = remote
                    .Select(e => new PfsEntry(e.Name, e.IsDirectory, string.IsNullOrEmpty(e.Details) ? (e.IsDirectory ? "目录" : "文件") : e.Details, (long)Math.Min(e.Size, long.MaxValue)))
                    .ToList();
            }
            else
            {
                var result = await _pfs.ListDirectoryAsync(device, partition, _currentPath, CancellationToken.None);
                AppendResult(result);
                if (!PfsShellClient.IsSuccessfulSession(result))
                    throw new InvalidOperationException($"无法打开 PFS 分区“{partition}”，请查看诊断输出。");
                entries = PfsShellClient.ParseDirectoryEntries(result.StandardOutput);
            }

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
            if (TryGetDevice(out var device))
                await UpdateDiskSpaceAsync(device, _activePartition);
            return;
        }

        if (!entry.IsDirectory) return;
        _currentPath = CombineRemotePath(_currentPath, entry.Name);
        await RefreshDirectoryAsync();
    }

    private async Task GoUpAsync()
    {
        if (_activePartition is null) return;
        var parts = PfsNameEncoding.SplitRemotePath(_currentPath).ToList();
        if (parts.Count == 0)
        {
            if (TryGetDevice(out var device) && PfsNetProtocol.IsNetworkDevice(device))
            {
                try { await _pfsNet.UmountAsync(CancellationToken.None); } catch { }
            }

            await LoadPartitionsAsync();
            return;
        }
        parts.RemoveAt(parts.Count - 1);
        _currentPath = parts.Count == 0 ? "/" : "/" + string.Join('/', parts);
        await RefreshDirectoryAsync();
    }

    // 可编辑文本框（含重命名编辑框、NBD IP）内保留 Backspace 删字；只读路径框等仍触发返回上级。
    private bool IsEditableTextInputFocused()
    {
        for (Control? control = ActiveControl; control is not null; control = control.Parent)
        {
            if (control is TextBox { ReadOnly: true })
                return false;
            if (control is TextBoxBase)
                return true;
            if (control is ComboBox { DropDownStyle: not ComboBoxStyle.DropDownList })
                return true;
        }

        return false;
    }

    private async Task ExportSelectedAsync()
    {
        if (SelectedEntry() is not { IsDirectory: false } entry) { ShowWarning("请选择一个文件。目录导出将在后续版本加入。"); return; }
        if (!TryGetPfsTarget(out var device, out var partition)) return;

        using var dialog = new FolderBrowserDialog { Description = "选择导出到的本地目录" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        BeginFileTransfer();
        var ct = BeginTransferCancellation();
        try
        {
            await RunBusyAsync($"正在导出 {PfsNameEncoding.ToDisplay(entry.Name)}…", async () =>
            {
                if (PfsNetProtocol.IsNetworkDevice(device))
                {
                    await _pfsNet.MountAsync(partition, CancellationToken.None);
                    var localPath = Path.Combine(dialog.SelectedPath, PfsNameEncoding.ToDisplay(entry.Name));
                    await _pfsNet.GetFileAsync(_currentPath, entry.Name, localPath, ct);
                    AppendLog($"已通过 PfsNet 导出：{localPath}");
                    return;
                }

                var result = await _pfs.ExportAsync(device, partition, _currentPath, entry.Name, dialog.SelectedPath, ct);
                AppendResult(result);
                if (!PfsShellClient.IsSuccessfulSession(result)) throw new InvalidOperationException("导出失败，请查看诊断输出。");
            });
        }
        finally
        {
            EndTransferCancellation();
            EndFileTransfer();
        }
    }

    private async Task ImportFileAsync()
    {
        using var dialog = new OpenFileDialog { Title = "导入文件到当前 PFS 目录", Multiselect = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await ImportLocalItemsAsync(dialog.FileNames);
    }

    private async Task PasteImportAsync()
    {
        if (!WindowsClipboardFiles.ContainsImportableItems())
        {
            ShowWarning("Windows 剪贴板中没有可导入的文件。请先在资源管理器中复制文件。");
            return;
        }

        var items = WindowsClipboardFiles.GetPaths();
        if (items.Length == 0)
        {
            ShowWarning("剪贴板中没有可导入的文件或目录。");
            return;
        }

        AppendLog($"从 Windows 剪贴板解析到 {items.Length} 个项目。");
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

        BeginFileTransfer();
        try
        {
            var hasConflicts = false;
            var entryCache = new Dictionary<string, Dictionary<string, PfsEntry>>(StringComparer.OrdinalIgnoreCase);
            await RunBusyAsync("正在扫描导入冲突…", async () =>
            {
                hasConflicts = await HasImportConflictsAsync(items, device, partition, _currentPath, entryCache);
            });

            if (hasConflicts && !OverwriteConfirmation.Show(this))
                return;

            ImportSpaceBudget? spaceBudget = null;
            await RunBusyAsync("正在检查当前 PFS 分区剩余空间…", async () =>
            {
                var currentSpace = await ReadPfsSpaceAsync(device, partition);
                // 覆盖时按“先删后拷”估算净占用，把将被替换项释放的空间计入可用额度。
                var requiredBytes = await EstimateNetImportRequirementAsync(
                    items, device, partition, _currentPath, entryCache, currentSpace.ZoneSizeBytes, overwrite: hasConflicts);
                if (requiredBytes > currentSpace.FreeBytes)
                {
                    AppendLog($"导入空间不足：当前分区剩余 {FormatBytes(currentSpace.FreeBytes)}，预计至少需要 {FormatBytes(requiredBytes)}。未启动文件写入。");
                    throw new InvalidOperationException("剩余空间不足！");
                }

                spaceBudget = new ImportSpaceBudget(currentSpace);
                AppendLog($"PFS 导入空间预检通过：当前分区剩余 {FormatBytes(currentSpace.FreeBytes)}，预计净占用 {FormatBytes(requiredBytes)}。");
            });
            if (spaceBudget is null)
                return;

            var progress = new ImportProgressState(CountImportFiles(items), SumImportBytes(items));
            BeginImportProgress(progress);
            var ct = BeginTransferCancellation();
            try
            {
                await RunBusyAsync($"正在导入 {items.Length} 个项目…", async () =>
                {
                    var pendingImports = new List<PfsImportRequest>();
                    foreach (var item in items)
                        await ImportLocalItemAsync(item, device, partition, _currentPath, entryCache, hasConflicts, progress, spaceBudget, pendingImports);
                    progress.Timer.Restart();
                    await ImportPendingFilesAsync(device, partition, pendingImports, progress, ct);
                    await RefreshDirectoryAsync();
                });
            }
            finally
            {
                // 先结束进度文案，再结束取消态，避免“正在取消…”被导入结束文案盖住或残留。
                EndImportProgress(progress);
                EndTransferCancellation();
            }
            await UpdateDiskSpaceAsync(device, partition);
        }
        finally
        {
            EndFileTransfer();
        }
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

        // 资源管理器风格：已选中后再点名称 → 等过双击判定后再重命名；
        // 若在双击间隔内又点一次：目录打开，文件则直接重命名（文件本来就不会“打开”）。
        private readonly System.Windows.Forms.Timer _labelEditTimer = new();
        private ListViewItem? _pendingEditItem;
        private bool _wasSelectedOnMouseDown;
        private Point _mouseDownPoint;

        public bool AllowNextLabelEdit { get; set; }

        public BrowserListView()
        {
            _labelEditTimer.Tick += (_, _) =>
            {
                _labelEditTimer.Stop();
                var item = _pendingEditItem;
                _pendingEditItem = null;
                if (item is null || item.ListView != this || !item.Selected || SelectedItems.Count != 1)
                    return;
                BeginRename(item);
            };
        }

        public void BeginRename(ListViewItem item)
        {
            if (item.ListView != this)
                return;
            AllowNextLabelEdit = true;
            FocusedItem = item;
            item.BeginEdit();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _labelEditTimer.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _labelEditTimer.Stop();
                _pendingEditItem = null;
                _mouseDownPoint = e.Location;
                var hit = HitTest(e.Location);
                _wasSelectedOnMouseDown = hit.Item is not null
                    && hit.Item.Selected
                    && SelectedItems.Count == 1;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !LabelEdit)
                return;

            var hit = HitTest(e.Location);
            if (!_wasSelectedOnMouseDown
                || hit.Item is null
                || SelectedItems.Count != 1
                || !hit.Item.Selected
                || hit.Item != GetItemAt(_mouseDownPoint.X, _mouseDownPoint.Y)
                || !IsRenameHit(hit))
                return;

            _pendingEditItem = hit.Item;
            _labelEditTimer.Interval = Math.Max(50, SystemInformation.DoubleClickTime);
            _labelEditTimer.Stop();
            _labelEditTimer.Start();
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            var pending = _pendingEditItem;
            _labelEditTimer.Stop();
            _pendingEditItem = null;

            // 文件没有“打开”，双击间隔内的第二次点击改为重命名，避免只闪一下没有反馈。
            if (pending is not null
                && pending.ListView == this
                && pending.Selected
                && SelectedItems.Count == 1
                && pending.Tag is PfsEntry { IsDirectory: false })
            {
                BeginRename(pending);
            }

            base.OnDoubleClick(e);
        }

        private static bool IsRenameHit(ListViewHitTestInfo hit) =>
            (hit.Location & (ListViewHitTestLocations.Label | ListViewHitTestLocations.Image)) != 0;

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

    private async Task DragSelectedToWindowsAsync()
    {
        var selected = SelectedEntries();
        if (selected.Count == 0 || _activePartition is null || !TryGetPfsTarget(out var device, out var partition))
            return;

        // PfsNet 拖出暂不支持（避免 staging 双写与 0KB 占位）；请用复制后粘贴。
        if (PfsNetProtocol.IsNetworkDevice(device))
        {
            MessageBox.Show(this, "网络传输不支持拖拽到PC", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var sourcePath = _currentPath;
        var sourceItems = selected
            .Select(entry => new PfsClipboardSelection(entry.Name, entry.IsDirectory, entry.Size))
            .ToArray();
        var progress = new VirtualExportProgressState();
        var stagingDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PS2-HDD-File-Manager",
            "Clipboard",
            Guid.NewGuid().ToString("N"));
        try
        {
            // 只在拖拽开始前读取目录结构，不读取文件内容。
            IReadOnlyList<DeferredPfsFile> files;
            if (sourceItems is [{ IsDirectory: false } singleFile])
            {
                files =
                [
                    new DeferredPfsFile(PfsNameEncoding.ToDisplay(singleFile.Name), sourcePath, singleFile.Name, false, singleFile.Size)
                ];
                progress.Initialize(files);
            }
            else
            {
                files = await BuildVirtualClipboardFilesAsync(device, partition, sourcePath, sourceItems, progress);
            }

            // 只缓存成功落地路径；取消/失败不得留下可复用的失败结果（Lazy 会缓存异常）。
            var materialized = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string MaterializeOnce(DeferredPfsFile file)
            {
                if (materialized.TryGetValue(file.RelativePath, out var existing))
                    return existing;
                var path = Task.Run(() => MaterializeVirtualClipboardFileAsync(device, partition, file, stagingDirectory, progress)).GetAwaiter().GetResult();
                materialized.TryAdd(file.RelativePath, path);
                return path;
            }
            var dataObject = new VirtualFileClipboardDataObject(
                () => files,
                MaterializeOnce,
                ex => ReportVirtualClipboardError(ex),
                deferStreams: true);

            // DoDragDrop 只负责等待用户松开鼠标；文件内容在目标确认放置后才由流读取。
            _entries.DoDragDrop(dataObject, DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            if (VirtualFileClipboardDataObject.IsCancelException(ex))
            {
                AppendLog("传输已取消。");
                _statusLabel.Text = "传输已取消";
                HideVirtualExportProgress();
                return;
            }
            AppendLog(ex.ToString());
            ShowWarning($"拖拽复制失败：{ex.Message}");
            HideVirtualExportProgress();
        }
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
        var sourceItems = selected.Select(entry => new PfsClipboardSelection(entry.Name, entry.IsDirectory, entry.Size)).ToArray();
        var exportProgress = new VirtualExportProgressState();
        var stagingDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PS2-HDD-File-Manager",
            "Clipboard",
            Guid.NewGuid().ToString("N"));

        IReadOnlyList<DeferredPfsFile> GetDeferredFiles()
        {
            if (sourceItems is not [{ IsDirectory: false } singleFile])
                return Task.Run(() => BuildVirtualClipboardFilesAsync(device, partition, sourcePath, sourceItems, exportProgress)).GetAwaiter().GetResult();

            var files = new[]
            {
                new DeferredPfsFile(PfsNameEncoding.ToDisplay(singleFile.Name), sourcePath, singleFile.Name, false, singleFile.Size)
            };
            exportProgress.Initialize(files);
            return files;
        }

        // PfsNet：粘贴时 IStream.Read 直拉网络 Get，无 staging 临时文件。
        if (PfsNetProtocol.IsNetworkDevice(device))
        {
            Stream OpenPfsNetClipboardStream(DeferredPfsFile file)
            {
                BeginFileTransfer();
                var completed = false;
                try
                {
                    UpdateVirtualExportProgress(exportProgress, file, 0, isCompleted: false);
                    var ct = EnsureTransferCancellationFromAnyThread();
                    return ProducerConsumerReadStream.Start(
                        Math.Max(file.Size, 0),
                        async (dest, token) =>
                        {
                            await _pfsNet.MountAsync(partition, CancellationToken.None);
                            await _pfsNet.GetFileToStreamAsync(
                                file.RemoteDirectory,
                                file.Name,
                                dest,
                                token,
                                bytes =>
                                {
                                    UpdateVirtualExportProgress(exportProgress, file, bytes, isCompleted: false);
                                    return Task.CompletedTask;
                                });
                        },
                        ct,
                        onSuccess: () =>
                        {
                            completed = true;
                            UpdateVirtualExportProgress(exportProgress, file, Math.Max(file.Size, 0), isCompleted: true);
                        },
                        onCleanup: () =>
                        {
                            if (!completed)
                            {
                                HideVirtualExportProgress();
                                EndTransferCancellationFromAnyThread();
                            }
                            EndFileTransfer();
                        });
                }
                catch
                {
                    HideVirtualExportProgress();
                    EndTransferCancellationFromAnyThread();
                    EndFileTransfer();
                    throw;
                }
            }

            dataObject = new VirtualFileClipboardDataObject(
                GetDeferredFiles,
                _ => throw new InvalidOperationException("PfsNet 剪贴板使用流式读取，不应落盘。"),
                ex => ReportVirtualClipboardError(ex),
                deferStreams: true,
                openStream: OpenPfsNetClipboardStream);
            successLog = $"已将 {sourceItems.Length} 个 PFS 项目放入 Windows 延迟流式传输（无临时落盘）。目标粘贴时才会通过网络读取。";
            return true;
        }

        // 只缓存成功落地路径；取消/失败不得留下可复用的失败结果（Lazy 会缓存异常）。
        var materialized = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string MaterializeOnce(DeferredPfsFile file)
        {
            if (materialized.TryGetValue(file.RelativePath, out var existing))
                return existing;
            var path = Task.Run(() => MaterializeVirtualClipboardFileAsync(device, partition, file, stagingDirectory, exportProgress)).GetAwaiter().GetResult();
            materialized.TryAdd(file.RelativePath, path);
            return path;
        }

        dataObject = new VirtualFileClipboardDataObject(
            GetDeferredFiles,
            MaterializeOnce,
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
            if (PfsNetProtocol.IsNetworkDevice(device))
            {
                await _pfsNet.MountAsync(partition, CancellationToken.None);
                var remoteName = PfsNameEncoding.ToPfsFileName(name);
                await _pfsNet.MkdirAsync(_currentPath, remoteName, CancellationToken.None);
            }
            else
            {
                var result = await _pfs.CreateDirectoryAsync(device, partition, _currentPath, name, CancellationToken.None);
                AppendResult(result);
                if (!PfsShellClient.IsSuccessfulSession(result)) throw new InvalidOperationException("创建目录失败，请查看诊断输出。");
            }

            await RefreshDirectoryAsync();
        });
        await UpdateDiskSpaceAsync(device, partition);
    }

    private Task RenameSelectedAsync()
    {
        if (_activePartition is null || _entries.SelectedItems.Count != 1 || _entries.SelectedItems[0].Tag is not PfsEntry)
        {
            ShowWarning("请先打开 PFS 分区，并选择要重命名的文件或目录。");
            return Task.CompletedTask;
        }
        _entries.FocusedItem = _entries.SelectedItems[0];
        ((BrowserListView)_entries).BeginRename(_entries.SelectedItems[0]);
        return Task.CompletedTask;
    }

    private async Task ApplyRenameAsync(string oldName, string newName)
    {
        if (!TryGetPfsTarget(out var device, out var partition)) return;
        await RunBusyAsync("正在重命名…", async () =>
        {
            if (PfsNetProtocol.IsNetworkDevice(device))
            {
                await _pfsNet.MountAsync(partition, CancellationToken.None);
                var remoteNew = PfsNameEncoding.ToPfsFileName(newName);
                await _pfsNet.RenameAsync(_currentPath, oldName, remoteNew, CancellationToken.None);
            }
            else
            {
                var result = await _pfs.RenameAsync(device, partition, _currentPath, oldName, newName, CancellationToken.None);
                AppendResult(result);
                if (!PfsShellClient.IsSuccessfulSession(result)) throw new InvalidOperationException("重命名失败，请查看诊断输出。");
            }

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
        await UpdateDiskSpaceAsync(device, partition);
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
        // 与实际 put/mkdir 一致：净化后按后端键名（PfsNet=Unicode，pfsshell=UTF-8 线名）查找。
        var entryKey = RemoteEntryKey(device, name);
        var entries = await GetRemoteEntriesAsync(device, partition, remoteDirectory, cache);
        var conflict = entries.TryGetValue(entryKey, out var existing);
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
        bool overwrite,
        ImportProgressState progress,
        ImportSpaceBudget spaceBudget,
        List<PfsImportRequest> pendingImports)
    {
        var name = LocalItemName(localItem);
        var entryKey = RemoteEntryKey(device, name);
        var isDirectory = Directory.Exists(localItem);
        var entries = await GetRemoteEntriesAsync(device, partition, remoteDirectory, cache);
        entries.TryGetValue(entryKey, out var existing);

        if (existing is not null && !overwrite)
            throw new InvalidOperationException($"目标中已存在“{name}”。请重新导入并选择全部覆盖。");

        if (isDirectory)
        {
            if (existing is { IsDirectory: false })
            {
                await DeleteRemoteFileAsync(device, partition, remoteDirectory, existing.Name);
                entries.Remove(entryKey);
                existing = null;
                spaceBudget.NeedsRefresh = true;
            }
            if (existing is null)
            {
                await ReservePfsSpaceAsync(device, partition, spaceBudget, spaceBudget.ZoneSizeBytes, name, isDirectory: true);
                await CreateRemoteDirectoryAsync(device, partition, remoteDirectory, name);
                entries[entryKey] = new PfsEntry(entryKey, true, string.Empty);
            }

            var childRemoteDirectory = CombineRemotePath(remoteDirectory, existing?.Name ?? entryKey);
            foreach (var child in Directory.EnumerateFileSystemEntries(localItem))
                await ImportLocalItemAsync(child, device, partition, childRemoteDirectory, cache, overwrite, progress, spaceBudget, pendingImports);
            return;
        }

        if (existing is { IsDirectory: true })
        {
            await DeleteRemoteDirectoryAsync(device, partition, remoteDirectory, existing.Name, cache);
            entries.Remove(entryKey);
            spaceBudget.NeedsRefresh = true;
        }
        else if (existing is not null)
        {
            await DeleteRemoteFileAsync(device, partition, remoteDirectory, existing.Name);
            entries.Remove(entryKey);
            spaceBudget.NeedsRefresh = true;
        }

        var fileLength = new FileInfo(localItem).Length;
        var fileRequirement = EstimateFileRequirement(fileLength, spaceBudget.ZoneSizeBytes);
        await ReservePfsSpaceAsync(device, partition, spaceBudget, fileRequirement, name, isDirectory: false);
        // 入队阶段不刷进度：碎文件会在扫描时把 UI 刷爆；真正写入时再报总体进度
        pendingImports.Add(new PfsImportRequest(remoteDirectory, localItem));
        entries[entryKey] = new PfsEntry(entryKey, false, string.Empty);
    }

    private async Task ImportPendingFilesAsync(
        string device,
        string partition,
        IReadOnlyList<PfsImportRequest> pendingImports,
        ImportProgressState progress,
        CancellationToken cancellationToken)
    {
        if (pendingImports.Count == 0)
            return;

        if (PfsNetProtocol.IsNetworkDevice(device))
        {
            await _pfsNet.MountAsync(partition, CancellationToken.None);
            var batchSw = System.Diagnostics.Stopwatch.StartNew();
            long batchTotalBytes = 0;
            for (var idx = 0; idx < pendingImports.Count; idx++)
            {
                var item = pendingImports[idx];
                cancellationToken.ThrowIfCancellationRequested();
                var fileLength = new FileInfo(item.LocalFilePath).Length;
                var remoteName = PfsNameEncoding.ToPfsFileName(Path.GetFileName(item.LocalFilePath));
                var fileSw = System.Diagnostics.Stopwatch.StartNew();
                await _pfsNet.PutFileAsync(
                    item.RemoteDirectory,
                    remoteName,
                    item.LocalFilePath,
                    overwrite: true,
                    cancellationToken,
                    bytes =>
                    {
                        // UI 限频在 SetImportProgress 内统一处理（碎文件按总体字节推进）
                        SetImportProgress(progress, item.LocalFilePath, Math.Clamp(bytes, 0, fileLength), isCompleted: false);
                        return Task.CompletedTask;
                    });
                fileSw.Stop();
                SetImportProgress(progress, item.LocalFilePath, fileLength, isCompleted: true);
                batchTotalBytes += fileLength;
                if (fileLength >= 1024 * 1024 || pendingImports.Count <= 32)
                {
                    var sec = Math.Max(0.001, fileSw.Elapsed.TotalSeconds);
                    var mbps = (fileLength / (1024.0 * 1024.0)) / sec;
                    AppendLog($"[{idx + 1}/{pendingImports.Count}] 已导入 {remoteName}（{FormatBytes(Math.Max(0, fileLength))}，用时 {fileSw.Elapsed:mm\\:ss}，{mbps:F2} MB/s）");
                }
            }

            batchSw.Stop();
            var batchSec = Math.Max(0.001, batchSw.Elapsed.TotalSeconds);
            var batchMbps = (batchTotalBytes / (1024.0 * 1024.0)) / batchSec;
            AppendLog($"已通过 PfsNet 导入 {pendingImports.Count} 个文件（共 {FormatBytes(Math.Max(0, batchTotalBytes))}，总用时 {batchSw.Elapsed:hh\\:mm\\:ss}，平均 {batchMbps:F2} MB/s）。");
            return;
        }

        // 同一目录内出现同名来源时，维持旧版逐文件覆盖语义，避免批处理改变最终结果。
        var distinctTargets = pendingImports
            .Select(item => item.RemoteDirectory + "\0" + PfsNameEncoding.ToShell(PfsNameEncoding.ToPfsFileName(Path.GetFileName(item.LocalFilePath))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() == pendingImports.Count;

        if (pendingImports.Count > 1 && distinctTargets)
        {
            var completed = new HashSet<PfsImportRequest>();
            var batch = await _pfs.ImportBatchAsync(
                device,
                partition,
                pendingImports,
                CancellationToken.None,
                async (item, bytes, isCompleted) =>
                {
                    var fileLength = new FileInfo(item.LocalFilePath).Length;
                    var transferred = Math.Clamp(bytes, 0, fileLength);
                    if (isCompleted)
                    {
                        if (completed.Add(item))
                            SetImportProgress(progress, item.LocalFilePath, fileLength, isCompleted: true);
                        return;
                    }

                    SetImportProgress(progress, item.LocalFilePath, transferred, isCompleted: false);
                    await Task.CompletedTask;
                });

            if (batch.UsedSharedSession)
            {
                if (batch.Result is null)
                    throw new InvalidOperationException("批量导入未返回执行结果。");
                AppendResult(batch.Result);
                if (!PfsShellClient.IsSuccessfulSession(batch.Result))
                    throw new InvalidOperationException("批量导入失败，请查看诊断输出。");

                // pfsshell 成功写完时会报告每个文件的最终字节数；此处仅补偿异常缺失的最终进度事件。
                foreach (var item in pendingImports.Where(item => !completed.Contains(item)))
                {
                    var fileLength = new FileInfo(item.LocalFilePath).Length;
                    SetImportProgress(progress, item.LocalFilePath, fileLength, isCompleted: true);
                }
                AppendLog($"已使用共享 PFS 会话连续导入 {pendingImports.Count} 个文件。");
                return;
            }

            AppendLog($"{batch.FallbackReason}，已切换为逐文件兼容导入。");
        }

        foreach (var item in pendingImports)
        {
            var fileLength = new FileInfo(item.LocalFilePath).Length;
            var import = await ImportFileWithProgressAsync(device, partition, item.RemoteDirectory, item.LocalFilePath, fileLength, progress);
            AppendResult(import);
            if (!PfsShellClient.IsSuccessfulSession(import))
                throw new InvalidOperationException($"导入“{Path.GetFileName(item.LocalFilePath)}”失败，请查看诊断输出。");
            SetImportProgress(progress, item.LocalFilePath, fileLength, isCompleted: true);
        }
    }

    // pfsshell 在每次成功写入一个传输块后报告累计字节数；
    // 直接使用该计数显示进度，避免写入期间反复扫描实体硬盘上的 PFS 位图。
    private async Task<ProcessResult> ImportFileWithProgressAsync(
        string device,
        string partition,
        string remoteDirectory,
        string localFile,
        long fileLength,
        ImportProgressState progress)
    {
        return await _pfs.ImportAsync(
            device,
            partition,
            remoteDirectory,
            localFile,
            CancellationToken.None,
            bytes =>
            {
                SetImportProgress(progress, localFile, Math.Clamp(bytes, 0, fileLength), isCompleted: false);
                return Task.CompletedTask;
            });
    }

    private async Task<Dictionary<string, PfsEntry>> GetRemoteEntriesAsync(
        string device,
        string partition,
        string remoteDirectory,
        Dictionary<string, Dictionary<string, PfsEntry>> cache)
    {
        if (cache.TryGetValue(remoteDirectory, out var entries)) return entries;
        if (PfsNetProtocol.IsNetworkDevice(device))
        {
            await _pfsNet.MountAsync(partition, CancellationToken.None);
            var remote = await _pfsNet.ListDirAsync(remoteDirectory, CancellationToken.None);
            entries = remote
                .Select(e => new PfsEntry(e.Name, e.IsDirectory, e.Details, (long)Math.Min(e.Size, long.MaxValue)))
                .ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
            cache[remoteDirectory] = entries;
            return entries;
        }

        var result = await _pfs.ListDirectoryAsync(device, partition, remoteDirectory, CancellationToken.None);
        AppendResult(result);
        if (!PfsShellClient.IsSuccessfulSession(result))
            throw new InvalidOperationException($"无法读取目标目录“{remoteDirectory}”，请查看诊断输出。");
        entries = PfsShellClient.ParseDirectoryEntries(result.StandardOutput)
            .ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
        cache[remoteDirectory] = entries;
        return entries;
    }

    private async Task<PfsSpace> ReadPfsSpaceAsync(string device, string partition)
    {
        if (PfsNetProtocol.IsNetworkDevice(device))
        {
            await _pfsNet.MountAsync(partition, CancellationToken.None);
            var space = await _pfsNet.GetPfsFreeAsync(CancellationToken.None);
            return new PfsSpace(
                (long)Math.Min(space.TotalBytes, long.MaxValue),
                (long)Math.Min(space.FreeBytes, long.MaxValue),
                space.ZoneSizeBytes == 0 ? 8192u : space.ZoneSizeBytes);
        }

        var layout = await _apaPartitionTableReader.ReadAsync(device, CancellationToken.None);
        var current = layout.Partitions.SingleOrDefault(item =>
            !item.IsSubPartition && item.Name.Equals(partition, StringComparison.OrdinalIgnoreCase));
        if (current is null)
            throw new InvalidOperationException($"无法在 APA 分区表中定位当前分区“{partition}”。");
        return await _pfsSpaceReader.ReadAsync(device, current, CancellationToken.None);
    }

    private async Task ReservePfsSpaceAsync(
        string device,
        string partition,
        ImportSpaceBudget budget,
        long requiredBytes,
        string name,
        bool isDirectory)
    {
        if (budget.NeedsRefresh)
            budget.Reset(await ReadPfsSpaceAsync(device, partition));

        if (requiredBytes > budget.AvailableBytes)
        {
            var kind = isDirectory ? "目录" : "文件";
            AppendLog($"导入空间不足：当前分区剩余 {FormatBytes(budget.AvailableBytes)}，{kind}“{name}”预计至少需要 {FormatBytes(requiredBytes)}。未启动该项目的写入。");
            throw new InvalidOperationException("剩余空间不足！");
        }

        budget.AvailableBytes -= requiredBytes;
    }

    private async Task<long> EstimateNetImportRequirementAsync(
        IEnumerable<string> localItems,
        string device,
        string partition,
        string remoteDirectory,
        Dictionary<string, Dictionary<string, PfsEntry>> cache,
        uint zoneSizeBytes,
        bool overwrite)
    {
        long required = 0;
        foreach (var item in localItems)
            required = checked(required + await EstimateNetImportRequirementAsync(item, device, partition, remoteDirectory, cache, zoneSizeBytes, overwrite));
        return required < 0 ? 0 : required;
    }

    private async Task<long> EstimateNetImportRequirementAsync(
        string localItem,
        string device,
        string partition,
        string remoteDirectory,
        Dictionary<string, Dictionary<string, PfsEntry>> cache,
        uint zoneSizeBytes,
        bool overwrite)
    {
        var name = LocalItemName(localItem);
        var entryKey = RemoteEntryKey(device, name);
        var isDirectory = Directory.Exists(localItem);
        var entries = await GetRemoteEntriesAsync(device, partition, remoteDirectory, cache);
        entries.TryGetValue(entryKey, out var existing);

        if (isDirectory)
        {
            long required = 0;
            if (existing is { IsDirectory: false })
            {
                if (overwrite)
                    required = checked(required - EstimateFileRequirement(existing.Size, zoneSizeBytes));
                existing = null;
            }

            // 远程尚无此目录（或刚删掉同名文件准备新建）：按本地树估算，勿 ls 未创建路径。
            if (existing is null)
                return checked(required + EstimateLocalImportRequirement(localItem, zoneSizeBytes));

            // 合并已有同名目录：只累计子项净需求。
            var childRemoteDirectory = CombineRemotePath(remoteDirectory, existing.Name);
            foreach (var child in Directory.EnumerateFileSystemEntries(localItem))
                required = checked(required + await EstimateNetImportRequirementAsync(child, device, partition, childRemoteDirectory, cache, zoneSizeBytes, overwrite));
            return required;
        }

        var fileRequirement = EstimateFileRequirement(new FileInfo(localItem).Length, zoneSizeBytes);
        if (!overwrite || existing is null)
            return fileRequirement;

        if (existing.IsDirectory)
        {
            var reclaimed = await EstimateRemoteTreeRequirementAsync(device, partition, remoteDirectory, existing.Name, cache, zoneSizeBytes);
            return checked(fileRequirement - reclaimed);
        }

        return checked(fileRequirement - EstimateFileRequirement(existing.Size, zoneSizeBytes));
    }

    private async Task<long> EstimateRemoteTreeRequirementAsync(
        string device,
        string partition,
        string parentDirectory,
        string name,
        Dictionary<string, Dictionary<string, PfsEntry>> cache,
        uint zoneSizeBytes)
    {
        // 删除整个远程目录树时，目录元数据与各文件分配均可回收。
        long required = zoneSizeBytes;
        var directory = CombineRemotePath(parentDirectory, name);
        var entries = await GetRemoteEntriesAsync(device, partition, directory, cache);
        foreach (var child in entries.Values)
        {
            if (child.IsDirectory)
                required = checked(required + await EstimateRemoteTreeRequirementAsync(device, partition, directory, child.Name, cache, zoneSizeBytes));
            else
                required = checked(required + EstimateFileRequirement(child.Size, zoneSizeBytes));
        }

        return required;
    }

    private static long EstimateLocalImportRequirement(string item, uint zoneSizeBytes)
    {
        if (File.Exists(item))
            return EstimateFileRequirement(new FileInfo(item).Length, zoneSizeBytes);
        if (!Directory.Exists(item))
            return 0;

        // 新目录自身按一个 zone 预留，再累加子项。
        long required = zoneSizeBytes;
        foreach (var child in Directory.EnumerateFileSystemEntries(item))
            required = checked(required + EstimateLocalImportRequirement(child, zoneSizeBytes));
        return required;
    }

    private static long EstimateFileRequirement(long fileLength, uint zoneSizeBytes)
    {
        if (fileLength < 0) throw new ArgumentOutOfRangeException(nameof(fileLength));
        var zone = (long)zoneSizeBytes;
        var dataAllocation = fileLength == 0 ? 0 : checked(((fileLength + zone - 1) / zone) * zone);
        // 额外预留一个 zone，覆盖 inode、目录项与分配粒度开销，避免“刚好放得下”仍触发 ENOSPC。
        return checked(dataAllocation + zone);
    }

    private async Task UpdateDiskSpaceAsync(string device, string? partition)
    {
        _diskSpaceLabel.Text = "硬盘总计：正在读取…　未分配：正在读取…　当前分区：—　当前分区剩余：—";
        try
        {
            if (PfsNetProtocol.IsNetworkDevice(device))
            {
                var apa = await _pfsNet.GetApaFreeAsync(CancellationToken.None);
                var label = $"硬盘总计：{FormatCapacity((long)Math.Min(apa.TotalBytes, long.MaxValue))}　未分配：{FormatCapacity((long)Math.Min(apa.FreeBytes, long.MaxValue))}";
                if (string.IsNullOrWhiteSpace(partition))
                {
                    _diskSpaceLabel.Text = label + "　当前分区：—　当前分区剩余：—";
                    return;
                }

                await _pfsNet.MountAsync(partition, CancellationToken.None);
                var pfs = await _pfsNet.GetPfsFreeAsync(CancellationToken.None);
                var partitionTotal = pfs.TotalBytes;
                if (partitionTotal == 0)
                {
                    // 兼容旧 ELF：GET_PFS_FREE 总容量为 0 时回退 LIST_PARTITIONS
                    var parts = await _pfsNet.ListPartitionsAsync(includeSystem: true, CancellationToken.None);
                    var match = parts.FirstOrDefault(p =>
                        p.Name.Equals(partition, StringComparison.OrdinalIgnoreCase));
                    if (match.TotalBytes > 0)
                        partitionTotal = match.TotalBytes;
                }

                _diskSpaceLabel.Text = label
                    + $"　当前分区：{FormatCapacity((long)Math.Min(partitionTotal, long.MaxValue))}"
                    + $"　当前分区剩余：{FormatCapacity((long)Math.Min(pfs.FreeBytes, long.MaxValue))}";
                return;
            }

            var layout = await _apaPartitionTableReader.ReadAsync(device, CancellationToken.None);
            var localLabel = $"硬盘总计：{FormatCapacity(layout.TotalBytes)}　未分配：{FormatCapacity(layout.FreeBytes)}";
            if (string.IsNullOrWhiteSpace(partition))
            {
                _diskSpaceLabel.Text = localLabel + "　当前分区：—　当前分区剩余：—";
                return;
            }

            var current = layout.Partitions.SingleOrDefault(item =>
                !item.IsSubPartition && item.Name.Equals(partition, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                _diskSpaceLabel.Text = localLabel + "　当前分区：无法读取　当前分区剩余：无法读取";
                AppendLog($"无法在 APA 分区表中定位当前分区：{partition}");
                return;
            }

            _diskSpaceLabel.Text = localLabel + $"　当前分区：{FormatCapacity(current.TotalBytes)}　当前分区剩余：正在读取…";
            var pfsSpace = await _pfsSpaceReader.ReadAsync(device, current, CancellationToken.None);
            _diskSpaceLabel.Text = localLabel + $"　当前分区：{FormatCapacity(pfsSpace.TotalBytes)}　当前分区剩余：{FormatCapacity(pfsSpace.FreeBytes)}";
        }
        catch (Exception ex)
        {
            _diskSpaceLabel.Text = "硬盘总计：无法读取　未分配：无法读取　当前分区：无法读取　当前分区剩余：无法读取";
            AppendLog($"无法读取容量信息：{ex.Message}");
        }
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

        if (PfsNetProtocol.IsNetworkDevice(device))
        {
            await _pfsNet.MountAsync(partition, CancellationToken.None);
            await _pfsNet.RmdirAsync(parentDirectory, name, CancellationToken.None);
        }
        else
        {
            var result = await _pfs.RemoveDirectoryAsync(device, partition, parentDirectory, name, CancellationToken.None);
            AppendResult(result);
            if (!PfsShellClient.IsSuccessfulSession(result))
                throw new InvalidOperationException($"无法删除目录“{name}”，请查看诊断输出。");
        }

        cache.Remove(directory);
    }

    private async Task DeleteRemoteFileAsync(string device, string partition, string remoteDirectory, string name)
    {
        if (PfsNetProtocol.IsNetworkDevice(device))
        {
            await _pfsNet.MountAsync(partition, CancellationToken.None);
            await _pfsNet.RemoveAsync(remoteDirectory, name, CancellationToken.None);
            return;
        }

        var result = await _pfs.DeleteAsync(device, partition, remoteDirectory, name, CancellationToken.None);
        AppendResult(result);
        if (!PfsShellClient.IsSuccessfulSession(result))
            throw new InvalidOperationException($"无法删除文件“{name}”，请查看诊断输出。");
    }

    private async Task CreateRemoteDirectoryAsync(string device, string partition, string remoteDirectory, string name)
    {
        if (PfsNetProtocol.IsNetworkDevice(device))
        {
            await _pfsNet.MountAsync(partition, CancellationToken.None);
            await _pfsNet.MkdirAsync(remoteDirectory, PfsNameEncoding.ToPfsFileName(name), CancellationToken.None);
            return;
        }

        var result = await _pfs.CreateDirectoryAsync(device, partition, remoteDirectory, name, CancellationToken.None);
        AppendResult(result);
        if (!PfsShellClient.IsSuccessfulSession(result))
            throw new InvalidOperationException($"无法创建目录“{name}”，请查看诊断输出。");
    }

    private async Task<IReadOnlyList<DeferredPfsFile>> BuildVirtualClipboardFilesAsync(
        string device,
        string partition,
        string sourcePath,
        IReadOnlyList<PfsClipboardSelection> selections,
        VirtualExportProgressState progress)
    {
        var files = new List<DeferredPfsFile>();
        foreach (var selection in selections)
        {
            if (selection.IsDirectory)
                await AddVirtualDirectoryAsync(device, partition, CombineRemotePath(sourcePath, selection.Name), PfsNameEncoding.ToDisplay(selection.Name), files);
            else
                files.Add(new DeferredPfsFile(PfsNameEncoding.ToDisplay(selection.Name), sourcePath, selection.Name, false, selection.Size));
        }
        if (files.Count == 0) throw new InvalidOperationException("没有可复制的 PFS 项目。");
        progress.Initialize(files);
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
        IReadOnlyList<PfsEntry> entries;
        if (PfsNetProtocol.IsNetworkDevice(device))
        {
            await _pfsNet.MountAsync(partition, CancellationToken.None);
            var remote = await _pfsNet.ListDirAsync(remoteDirectory, CancellationToken.None);
            entries = remote
                .Select(e => new PfsEntry(e.Name, e.IsDirectory, e.Details, (long)Math.Min(e.Size, long.MaxValue)))
                .ToList();
        }
        else
        {
            var result = await _pfs.ListDirectoryAsync(device, partition, remoteDirectory, CancellationToken.None);
            if (!PfsShellClient.IsSuccessfulSession(result))
                throw new InvalidOperationException($"无法读取 PFS 目录“{relativePath}”。连接可能已断开。");
            entries = PfsShellClient.ParseDirectoryEntries(result.StandardOutput);
        }

        foreach (var entry in entries)
        {
            var childRelativePath = CombineRemotePath(relativePath, PfsNameEncoding.ToDisplay(entry.Name));
            if (entry.IsDirectory)
                await AddVirtualDirectoryAsync(device, partition, CombineRemotePath(remoteDirectory, entry.Name), childRelativePath, files);
            else
                files.Add(new DeferredPfsFile(childRelativePath, remoteDirectory, entry.Name, false, entry.Size));
        }
    }

    private async Task<string> MaterializeVirtualClipboardFileAsync(
        string device,
        string partition,
        DeferredPfsFile file,
        string stagingDirectory,
        VirtualExportProgressState progress)
    {
        BeginFileTransfer();
        var completed = false;
        try
        {
            var relativeDirectory = Path.GetDirectoryName(file.RelativePath.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
            var destination = Path.Combine(stagingDirectory, relativeDirectory);
            Directory.CreateDirectory(destination);
            UpdateVirtualExportProgress(progress, file, 0, isCompleted: false);
            // 剪贴板/拖出落地时展示取消按钮（COM 路径尽力支持）。
            var ct = EnsureTransferCancellationFromAnyThread();
            var localPath = Path.Combine(destination, PfsNameEncoding.ToDisplay(file.Name));
            if (PfsNetProtocol.IsNetworkDevice(device))
            {
                await _pfsNet.MountAsync(partition, CancellationToken.None);
                await _pfsNet.GetFileAsync(
                    file.RemoteDirectory,
                    file.Name,
                    localPath,
                    ct,
                    bytes =>
                    {
                        UpdateVirtualExportProgress(progress, file, bytes, isCompleted: false);
                        return Task.CompletedTask;
                    });
            }
            else
            {
                var result = await _pfs.ExportAsync(
                    device,
                    partition,
                    file.RemoteDirectory,
                    file.Name,
                    destination,
                    ct,
                    bytes =>
                    {
                        UpdateVirtualExportProgress(progress, file, bytes, isCompleted: false);
                        return Task.CompletedTask;
                    });
                if (!File.Exists(localPath))
                {
                    if (!string.IsNullOrWhiteSpace(result.CombinedOutput))
                        AppendResult(result);
                    throw new InvalidOperationException($"无法读取 PFS 文件“{file.RelativePath}”。连接可能已断开。");
                }
            }

            if (!File.Exists(localPath))
                throw new InvalidOperationException($"无法读取 PFS 文件“{file.RelativePath}”。连接可能已断开。");
            UpdateVirtualExportProgress(progress, file, new FileInfo(localPath).Length, isCompleted: true);
            completed = true;
            return localPath;
        }
        finally
        {
            if (!completed)
            {
                HideVirtualExportProgress();
                EndTransferCancellationFromAnyThread();
            }
            EndFileTransfer();
        }
    }

    private void ReportVirtualClipboardError(Exception error)
    {
        if (IsDisposed || !IsHandleCreated) return;
        // Task.Run / COM 路径偶发包一层 AggregateException，仍按取消处理。
        while (error is AggregateException { InnerException: { } inner })
            error = inner;
        BeginInvoke(() =>
        {
            // CTS 已由 MaterializeVirtualClipboardFileAsync 的 finally 清理；此处不再 End，避免迟到回调清掉新一轮传输。
            if (VirtualFileClipboardDataObject.IsCancelException(error))
            {
                AppendLog("传输已取消。");
                _statusLabel.Text = "传输已取消";
                return;
            }
            AppendLog(error.ToString());
            ShowWarning($"粘贴失败：{error.Message}");
        });
    }

    private static string LocalItemName(string path) => Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>导入冲突/缓存键：PfsNet 用 Unicode PFS 名；pfsshell 用 UTF-8 线名。</summary>
    private static string RemoteEntryKey(string device, string localName)
    {
        var pfsName = PfsNameEncoding.ToPfsFileName(localName);
        return PfsNetProtocol.IsNetworkDevice(device) ? pfsName : PfsNameEncoding.ToShell(pfsName);
    }

    private static bool ClipboardHasImportableItems() => WindowsClipboardFiles.ContainsImportableItems();

    private static int CountImportFiles(IEnumerable<string> items)
    {
        var count = 0;
        foreach (var item in items)
        {
            if (File.Exists(item))
            {
                count++;
                continue;
            }

            if (Directory.Exists(item))
                count += Directory.EnumerateFiles(item, "*", SearchOption.AllDirectories).Count();
        }
        return count;
    }

    private static long SumImportBytes(IEnumerable<string> items)
    {
        long total = 0;
        foreach (var item in items)
        {
            if (File.Exists(item))
            {
                total = checked(total + new FileInfo(item).Length);
                continue;
            }

            if (!Directory.Exists(item))
                continue;

            foreach (var file in Directory.EnumerateFiles(item, "*", SearchOption.AllDirectories))
                total = checked(total + new FileInfo(file).Length);
        }
        return total;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(bytes, 0);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    private static string FormatCapacity(long bytes)
    {
        var value = Math.Max(bytes, 0);
        const long mebibyte = 1024L * 1024;
        const long gibibyte = 1024L * mebibyte;
        return value < gibibyte
            ? $"{value / (double)mebibyte:0.##}MB"
            : $"{value / (double)gibibyte:0.##}GB";
    }

    private static string FormatPartitionSize(long bytes)
    {
        const long mebibyte = 1024L * 1024;
        const long gibibyte = 1024L * mebibyte;
        return bytes >= gibibyte && bytes % gibibyte == 0
            ? $"{bytes / gibibyte}G"
            : $"{bytes / mebibyte}M";
    }

    private static string FormatTransferSpeed(double bytesPerSecond)
    {
        const double mebibyte = 1024d * 1024;
        const double gibibyte = 1024d * mebibyte;
        return bytesPerSecond < gibibyte
            ? $"{Math.Max(bytesPerSecond, 0) / mebibyte:0.##} MB/s"
            : $"{bytesPerSecond / gibibyte:0.##} GB/s";
    }

    private static bool IsSafePartitionName(string name, out string error)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[+A-Za-z0-9_.-]{1,32}$"))
        {
            error = "分区名只能使用英文、数字、加号、点、下划线或连字符；总长度最多 32 个字符。例如：DATA 或 +DATA。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryParsePartitionSize(string value, out long bytes, out string normalizedSize, out string error)
    {
        bytes = 0;
        normalizedSize = string.Empty;
        error = string.Empty;
        var match = System.Text.RegularExpressions.Regex.Match(value.Trim(), @"^(?<value>\d+)\s*(?<unit>[mMgG])$");
        if (!match.Success)
        {
            error = "容量格式应为整数加 M 或 G，例如 512G、128G、512M。";
            return false;
        }

        if (!long.TryParse(match.Groups["value"].Value, out var amount) || amount <= 0)
        {
            error = "容量必须是正整数。";
            return false;
        }

        var unit = char.ToUpperInvariant(match.Groups["unit"].Value[0]);
        try
        {
            bytes = checked(amount * (unit == 'G' ? 1024L * 1024 * 1024 : 1024L * 1024));
        }
        catch (OverflowException)
        {
            error = "容量数值过大。";
            return false;
        }

        const long apaUnit = 128L * 1024 * 1024;
        const long apaMaximum = 2L * 1024 * 1024 * 1024 * 1024;
        if (bytes < apaUnit || bytes % apaUnit != 0)
        {
            error = "APA/PFS 分区容量必须是 128MiB 的整数倍，最小为 128M。";
            return false;
        }
        if (bytes > apaMaximum)
        {
            error = "传统 APA/PFS 的单个逻辑卷最大为 2TiB。";
            return false;
        }

        normalizedSize = unit == 'G' ? $"{amount}G" : $"{amount}M";
        return true;
    }

    private void BeginImportProgress(ImportProgressState progress)
    {
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 1000;
        _progressBar.Value = 0;
        _progressBar.Visible = true;
        _transferSpeedLabel.Text = "0 MB/s";
        _transferSpeedLabel.Visible = true;
        _statusLabel.Text = progress.TotalFiles == 0
            ? "正在导入目录结构…"
            : $"准备导入：0 / {FormatBytes(progress.TotalBytes)} · {progress.TotalFiles} 个文件";
    }

    private void SetImportProgress(ImportProgressState progress, string localFilePath, long currentFileBytes, bool isCompleted)
    {
        // 计数在调用线程同步更新，避免 ConfigureAwait(false) 后 BeginInvoke 晚于 EndImportProgress
        if (isCompleted)
        {
            progress.CompletedFiles++;
            progress.CompletedBytes = Math.Min(progress.TotalBytes, progress.CompletedBytes + currentFileBytes);
        }

        // 碎文件会每文件触发回调；限制 UI 刷新频率，避免 BeginInvoke 塞满消息队列导致卡顿
        var allDone = progress.TotalFiles > 0 && progress.CompletedFiles >= progress.TotalFiles;
        if (!allDone && !progress.ShouldPostUi(ProgressUiMinIntervalMs))
            return;

        var fileName = Path.GetFileName(localFilePath);
        var displayedBytes = Math.Min(progress.TotalBytes, progress.CompletedBytes + (isCompleted ? 0 : Math.Max(0, currentFileBytes)));
        var completedFiles = progress.CompletedFiles;
        var totalFiles = progress.TotalFiles;
        var totalBytes = progress.TotalBytes;
        var elapsedSeconds = Math.Max(progress.Timer.Elapsed.TotalSeconds, 0.001);
        var action = isCompleted ? "已导入" : "正在导入";
        var displayedFileNumber = isCompleted ? completedFiles : completedFiles + 1;

        void Update()
        {
            // 总字节未知时按文件数显示总体进度，避免条一直停在 0
            var fraction = totalBytes > 0
                ? (double)displayedBytes / totalBytes
                : (totalFiles > 0 ? (double)completedFiles / totalFiles : 0d);
            _progressBar.Value = Math.Clamp((int)Math.Round(fraction * _progressBar.Maximum), 0, _progressBar.Maximum);
            _transferSpeedLabel.Text = FormatTransferSpeed(displayedBytes / elapsedSeconds);
            _statusLabel.Text = totalBytes > 0
                ? $"{action}：{FormatBytes(displayedBytes)} / {FormatBytes(totalBytes)} · {displayedFileNumber} / {totalFiles} 个文件 · {fileName}"
                : $"{action}：{displayedFileNumber} / {totalFiles} 个文件 · {fileName}";
        }

        if (InvokeRequired)
            BeginInvoke((Action)Update);
        else
            Update();
    }

    private void EndImportProgress(ImportProgressState progress)
    {
        _progressBar.Visible = false;
        _transferSpeedLabel.Visible = false;
        _statusLabel.Text = progress.CompletedFiles == progress.TotalFiles
            ? $"导入完成：{progress.CompletedFiles} 个文件 · {FormatBytes(progress.CompletedBytes)}"
            : $"导入结束：{progress.CompletedFiles} / {progress.TotalFiles} 个文件 · {FormatBytes(progress.CompletedBytes)}";
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
    private void AppendResult(ProcessResult result) => AppendLog(PfsNameEncoding.ToDisplay(result.CombinedOutput));
    private void AppendLog(string message) => _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {PfsNameEncoding.TrimAscii(message)}{Environment.NewLine}");
    private void ShowWarning(string message) => MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    private void ShowInfo(string message) => MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void UpdateVirtualExportProgress(
        VirtualExportProgressState progress,
        DeferredPfsFile file,
        long currentBytes,
        bool isCompleted)
    {
        var snapshot = progress.Update(file, currentBytes, isCompleted);
        if (IsDisposed || !IsHandleCreated)
            return;

        // 碎文件/每块回调都会进这里；限频刷新，保留总体字节进度
        if (!snapshot.IsCompletedAll && !progress.ShouldPostUi(ProgressUiMinIntervalMs))
            return;

        void Update()
        {
            if (IsDisposed)
                return;
            _progressBar.Minimum = 0;
            _progressBar.Maximum = 1000;
            _progressBar.Value = snapshot.TotalBytes > 0
                ? Math.Clamp((int)Math.Round((double)snapshot.DisplayedBytes / snapshot.TotalBytes * 1000), 0, 1000)
                : (snapshot.TotalFiles <= 0
                    ? 0
                    : Math.Clamp((int)Math.Round((double)snapshot.CompletedFiles / snapshot.TotalFiles * 1000), 0, 1000));
            _progressBar.Visible = !snapshot.IsCompletedAll;
            _transferSpeedLabel.Text = FormatTransferSpeed(snapshot.SpeedBytesPerSecond);
            _transferSpeedLabel.Visible = !snapshot.IsCompletedAll;
            _statusLabel.Text = snapshot.IsCompletedAll
                ? (snapshot.TotalBytes > 0
                    ? $"复制到 Windows 完成：{FormatBytes(snapshot.TotalBytes)}"
                    : $"复制到 Windows 完成：{snapshot.TotalFiles} 个文件")
                : (snapshot.TotalBytes > 0
                    ? $"复制到 PC：{FormatBytes(snapshot.DisplayedBytes)} / {FormatBytes(snapshot.TotalBytes)} · {snapshot.CompletedFiles} / {snapshot.TotalFiles}"
                    : $"复制到 PC：{snapshot.CompletedFiles} / {snapshot.TotalFiles} 个文件");
            if (snapshot.IsCompletedAll)
                EndTransferCancellation();
        }

        if (InvokeRequired)
            BeginInvoke((Action)Update);
        else
            Update();
    }

    private void HideVirtualExportProgress()
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        void Hide()
        {
            if (IsDisposed)
                return;
            _progressBar.Visible = false;
            _transferSpeedLabel.Visible = false;
            EndTransferCancellation();
        }

        if (InvokeRequired)
            BeginInvoke((Action)Hide);
        else
            Hide();
    }

    private void BeginFileTransfer()
    {
        Interlocked.Increment(ref _fileTransferDepth);
        UpdateSourceRefreshEnabled();
    }

    private void EndFileTransfer()
    {
        Interlocked.Decrement(ref _fileTransferDepth);
        UpdateSourceRefreshEnabled();
    }

    private void UpdateSourceRefreshEnabled()
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        void Update()
        {
            if (!IsDisposed)
                _refreshDisksButton.Enabled = Volatile.Read(ref _fileTransferDepth) == 0;
        }

        if (InvokeRequired)
            BeginInvoke((Action)Update);
        else
            Update();
    }

    private async Task RunBusyAsync(string status, Func<Task> action)
    {
        var isOutermostOperation = _busyDepth++ == 0;
        if (isOutermostOperation)
        {
            UseWaitCursor = true;
            _busyControlStates = Controls
                .Cast<Control>()
                .Where(control => control != _statusBar)
                .ToDictionary(control => control, control => control.Enabled);
            foreach (var control in _busyControlStates.Keys)
                control.Enabled = false;
        }
        AppendLog(status);
        try { await action(); }
        catch (OperationCanceledException)
        {
            AppendLog("传输已取消。");
            _statusLabel.Text = "传输已取消";
        }
        catch (Exception ex) { AppendLog(ex.ToString()); ShowWarning(ex.Message); }
        finally
        {
            if (--_busyDepth == 0)
            {
                if (_busyControlStates is not null)
                {
                    foreach (var (control, wasEnabled) in _busyControlStates)
                        control.Enabled = wasEnabled;
                    _busyControlStates = null;
                }
                UseWaitCursor = false;
            }
        }
    }

    private CancellationToken BeginTransferCancellation()
    {
        EndTransferCancellation();
        _transferCts = new CancellationTokenSource();
        _cancelTransferButton.Visible = true;
        _cancelTransferButton.Enabled = true;
        return _transferCts.Token;
    }

    private void EndTransferCancellation()
    {
        var canceled = _transferCts?.IsCancellationRequested == true;
        _cancelTransferButton.Visible = false;
        _cancelTransferButton.Enabled = false;
        var cts = _transferCts;
        _transferCts = null;
        cts?.Dispose();
        // 取消已完成后清掉“正在取消…”，避免按钮已隐藏但状态栏仍卡住。
        if (canceled)
            _statusLabel.Text = "传输已取消";
    }

    private CancellationToken EnsureTransferCancellationFromAnyThread()
    {
        if (IsDisposed)
            return CancellationToken.None;
        if (InvokeRequired)
            return (CancellationToken)Invoke(EnsureTransferCancellationFromAnyThread);
        return _transferCts?.Token ?? BeginTransferCancellation();
    }

    private void EndTransferCancellationFromAnyThread()
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        // 捕获当前 CTS，避免 BeginInvoke 迟到时清掉新一轮传输。
        var expected = _transferCts;
        if (InvokeRequired)
        {
            BeginInvoke(() =>
            {
                if (expected is not null && !ReferenceEquals(_transferCts, expected))
                    return;
                EndTransferCancellation();
            });
            return;
        }
        EndTransferCancellation();
    }

    private void RequestCancelTransfer()
    {
        try { _transferCts?.Cancel(); }
        catch { /* 忽略重复取消 */ }
        if (_cancelTransferButton.Visible)
        {
            _cancelTransferButton.Enabled = false;
            _statusLabel.Text = "正在取消传输…";
            AppendLog("用户请求取消传输。");
        }
    }

    private static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PS2-HDD-File-Manager");

    private static string LastPfsNetIpPath => Path.Combine(AppDataDirectory, "last-pfsnet-ip.txt");

    private static string? LoadLastPfsNetIp()
    {
        try
        {
            if (!File.Exists(LastPfsNetIpPath))
                return null;
            var ip = File.ReadAllText(LastPfsNetIpPath).Trim();
            return string.IsNullOrWhiteSpace(ip) ? null : ip;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveLastPfsNetIp(string ipAddress)
    {
        try
        {
            Directory.CreateDirectory(AppDataDirectory);
            File.WriteAllText(LastPfsNetIpPath, ipAddress);
        }
        catch
        {
            /* 设置写入失败不影响连接 */
        }
    }

    private sealed record DeviceItem(string Path, string Display) { public override string ToString() => Display; }
    private sealed record PfsClipboardSelection(string Name, bool IsDirectory, long Size);

    // 进度条/状态栏刷新下限（毫秒）；碎文件传输时避免每文件 BeginInvoke
    private const int ProgressUiMinIntervalMs = 150;

    private sealed class VirtualExportProgressState
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _completedPaths = new(StringComparer.OrdinalIgnoreCase);
        private Stopwatch? _timer;
        private Stopwatch? _uiThrottle;
        private long _totalBytes;
        private long _completedBytes;
        private int _totalFiles;
        private int _completedFiles;

        public void Initialize(IReadOnlyList<DeferredPfsFile> files)
        {
            lock (_gate)
            {
                _totalBytes = files.Where(file => !file.IsDirectory).Sum(file => Math.Max(file.Size, 0));
                _totalFiles = files.Count(file => !file.IsDirectory);
                _completedBytes = 0;
                _completedFiles = 0;
                _completedPaths.Clear();
                _timer = null;
                _uiThrottle = null;
            }
        }

        public bool ShouldPostUi(int minIntervalMs)
        {
            lock (_gate)
            {
                if (_uiThrottle is null)
                {
                    _uiThrottle = Stopwatch.StartNew();
                    return true;
                }

                if (_uiThrottle.ElapsedMilliseconds < minIntervalMs)
                    return false;
                _uiThrottle.Restart();
                return true;
            }
        }

        public VirtualExportProgressSnapshot Update(DeferredPfsFile file, long currentBytes, bool isCompleted)
        {
            lock (_gate)
            {
                _timer ??= Stopwatch.StartNew();
                var current = Math.Max(currentBytes, 0);
                // 列表尺寸未知时允许累计已传字节，进度条仍按总体推进
                var displayed = _totalBytes > 0
                    ? Math.Min(_totalBytes, _completedBytes + current)
                    : _completedBytes + current;
                if (isCompleted && _completedPaths.Add(file.RelativePath))
                {
                    _completedFiles++;
                    var add = file.Size > 0 ? file.Size : current;
                    if (_totalBytes > 0)
                        _completedBytes = Math.Min(_totalBytes, _completedBytes + add);
                    else
                        _completedBytes += add;
                    displayed = _completedBytes;
                }

                var elapsed = Math.Max(_timer.Elapsed.TotalSeconds, 0.001);
                var allDone = _totalFiles > 0 && _completedFiles >= _totalFiles;
                return new VirtualExportProgressSnapshot(
                    _totalBytes,
                    displayed,
                    _completedFiles,
                    _totalFiles,
                    displayed / elapsed,
                    allDone);
            }
        }
    }

    private sealed record VirtualExportProgressSnapshot(
        long TotalBytes,
        long DisplayedBytes,
        int CompletedFiles,
        int TotalFiles,
        double SpeedBytesPerSecond,
        bool IsCompletedAll)
    {
        public bool IsCompleted => IsCompletedAll;
    }

    private sealed class ImportProgressState(int totalFiles, long totalBytes)
    {
        public int TotalFiles { get; } = totalFiles;
        public long TotalBytes { get; } = totalBytes;
        public int CompletedFiles { get; set; }
        public long CompletedBytes { get; set; }
        public Stopwatch Timer { get; } = Stopwatch.StartNew();
        private Stopwatch? _uiThrottle;

        public bool ShouldPostUi(int minIntervalMs)
        {
            if (_uiThrottle is null)
            {
                _uiThrottle = Stopwatch.StartNew();
                return true;
            }

            if (_uiThrottle.ElapsedMilliseconds < minIntervalMs)
                return false;
            _uiThrottle.Restart();
            return true;
        }
    }

    private sealed class ImportSpaceBudget
    {
        public ImportSpaceBudget(PfsSpace space) => Reset(space);

        public long AvailableBytes { get; set; }
        public uint ZoneSizeBytes { get; private set; }
        public bool NeedsRefresh { get; set; }

        public void Reset(PfsSpace space)
        {
            AvailableBytes = space.FreeBytes;
            ZoneSizeBytes = space.ZoneSizeBytes;
            NeedsRefresh = false;
        }
    }
}

internal static class ControlExtensions
{
    public static T Also<T>(this T control, Action<T> action) where T : Control { action(control); return control; }
}

internal static class Prompt
{
    public static string? Show(IWin32Window owner, string title, string label, string initialValue = "")
    {
        using var dialog = new Form { Text = title, ClientSize = new Size(560, 220), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false, AutoScaleMode = AutoScaleMode.Dpi };
        var textBox = new TextBox { Left = 24, Top = 68, Width = 512, Height = 34, Text = initialValue };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Left = 236, Top = 150, Width = 150, Height = 52 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Left = 386, Top = 150, Width = 150, Height = 52 };
        dialog.Controls.AddRange([new Label { Text = label, Left = 18, Top = 18, AutoSize = true }, textBox, ok, cancel]);
        dialog.AcceptButton = ok; dialog.CancelButton = cancel;
        return dialog.ShowDialog(owner) == DialogResult.OK ? textBox.Text.Trim() : null;
    }
}

internal static class OverwriteConfirmation
{
    public static bool Show(IWin32Window owner)
    {
        using var dialog = new Form { Text = "发现同名项目", ClientSize = new Size(620, 240), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false, AutoScaleMode = AutoScaleMode.Dpi };
        var label = new Label { Text = "目标 PFS 目录中发现同名文件或目录。\n选择“全部覆盖”将替换同名文件，并合并同名目录。", Left = 24, Top = 24, Width = 572, Height = 92 };
        var overwrite = new Button { Text = "全部覆盖", DialogResult = DialogResult.OK, Left = 300, Top = 164, Width = 150, Height = 52 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Left = 466, Top = 164, Width = 150, Height = 52 };
        dialog.Controls.AddRange([label, overwrite, cancel]);
        dialog.AcceptButton = overwrite;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }
}

internal sealed record PartitionCreateRequest(string Name, string Size);

internal static class PartitionCreatePrompt
{
    public static PartitionCreateRequest? Show(IWin32Window owner, string freeSpace, string defaultSize)
    {
        using var dialog = new Form
        {
            Text = "新建 PFS 分区",
            ClientSize = new Size(680, 390),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            AutoScaleMode = AutoScaleMode.Dpi
        };
        var description = new Label
        {
            Text = $"当前 APA 未分配空间：{freeSpace}\n分区名可使用 DATA 或 +DATA；容量必须是 128MiB 的整数倍。\n大于单个 APA 分区上限时，会由 PS2SDK 自动建立主分区和子分区。",
            Left = 18,
            Top = 15,
            Width = 632,
            Height = 108
        };
        var nameBox = new TextBox { Left = 164, Top = 148, Width = 488, Height = 38, Text = "+DATA" };
        var sizeBox = new TextBox { Left = 164, Top = 214, Width = 488, Height = 38, Text = defaultSize };
        var ok = new Button { Text = "下一步", DialogResult = DialogResult.OK, Left = 360, Top = 318, Width = 150, Height = 52 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Left = 520, Top = 318, Width = 150, Height = 52 };
        dialog.Controls.AddRange(
        [
            description,
            new Label { Text = "分区名称：", Left = 24, Top = 153, AutoSize = true }, nameBox,
            new Label { Text = "容量：", Left = 24, Top = 219, AutoSize = true }, sizeBox,
            new Label { Text = "例如：512G 或 128M", Left = 24, Top = 273, AutoSize = true, ForeColor = SystemColors.GrayText },
            ok,
            cancel
        ]);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog(owner) == DialogResult.OK
            ? new PartitionCreateRequest(nameBox.Text.Trim(), sizeBox.Text.Trim())
            : null;
    }
}

internal static class PartitionCreateConfirmation
{
    public static bool Show(IWin32Window owner, string source, string name, string size)
    {
        using var dialog = new Form
        {
            Text = "确认写入 APA 分区表",
            ClientSize = new Size(720, 400),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            AutoScaleMode = AutoScaleMode.Dpi
        };
        var message = new Label
        {
            Text = $"将在以下来源的“当前未分配 APA 空间”中创建新的 PFS 逻辑卷：\n{source}\n\n分区：{name}\n容量：{size}\n\n此操作会写入分区表。请勿断电、拔出硬盘或强制关闭程序。",
            Left = 18,
            Top = 16,
            Width = 672,
            AutoSize = false,
            AutoEllipsis = false
        };
        var measuredMessage = TextRenderer.MeasureText(
            message.Text,
            dialog.Font,
            new Size(message.Width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        message.Height = Math.Max(190, measuredMessage.Height + 16);

        var acknowledgeTop = message.Bottom + 18;
        var acknowledge = new CheckBox { Text = "我确认目标来源正确，并已备份重要数据", Left = 24, Top = acknowledgeTop, Width = 520, Height = 38 };
        var buttonTop = acknowledge.Bottom + 20;
        var create = new Button { Text = "创建", DialogResult = DialogResult.OK, Left = 380, Top = buttonTop, Width = 150, Height = 52, Enabled = false };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Left = 548, Top = buttonTop, Width = 150, Height = 52 };
        dialog.ClientSize = new Size(dialog.ClientSize.Width, buttonTop + 52 + 20);
        acknowledge.CheckedChanged += (_, _) => create.Enabled = acknowledge.Checked;
        dialog.Controls.AddRange([message, acknowledge, create, cancel]);
        dialog.AcceptButton = create;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }
}

internal sealed class ToolStripSpeedLabel : ToolStripControlHost
{
    private Label SpeedControl => (Label)Control;

    public ToolStripSpeedLabel() : base(new Label
    {
        Dock = DockStyle.Fill,
        AutoSize = false,
        Size = new Size(240, 18),
        MinimumSize = new Size(240, 18),
        TextAlign = ContentAlignment.MiddleRight,
        BackColor = SystemColors.Control,
        ForeColor = SystemColors.ControlText,
        Padding = new Padding(4, 0, 4, 0)
    })
    {
        AutoSize = false;
        Size = new Size(240, 18);
        Margin = new Padding(2, 2, 10, 2);
    }

    public new string Text
    {
        get => SpeedControl.Text;
        set => SpeedControl.Text = value;
    }

    public void BringControlToFront() => SpeedControl.BringToFront();
}
