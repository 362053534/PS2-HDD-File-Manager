using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PfsExplorerLite.Models;
using PfsExplorerLite.Services;
using PfsExplorerLite.Services.PfsNet;

namespace PfsExplorerLite;

internal sealed record BrowserRowItem(
    string DisplayName,
    string TypeText,
    string DetailsText,
    PfsEntry Entry);

internal sealed record DeferredPfsFile(
    string RelativePath,
    string RemoteDirectory,
    string Name,
    bool IsDirectory,
    long Size = 0);

internal sealed class MainWindow : Window
{
    private readonly PfsShellClient _pfs;
    private readonly NbdClient _nbd;
    private readonly PfsNetClient _pfsNet = new();
    private readonly DiskDiscovery _diskDiscovery;
    private readonly ApaSpaceReader _apaSpaceReader;
    private readonly ApaPartitionTableReader _apaPartitionTableReader;
    private readonly PfsSpaceReader _pfsSpaceReader;

    private readonly ComboBox _deviceCombo = new() { Width = 549, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _refreshDisksButton = new() { Content = "刷新", Margin = new Thickness(4) };
    private readonly TextBox _pathBox = new() { IsReadOnly = true, Width = 510, Text = "/", VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _showSystemPartitionsCheckBox = new()
    {
        Content = "显示系统分区",
        IsChecked = false,
        Margin = new Thickness(12, 0, 4, 0),
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _diskSpaceLabel = new()
    {
        Margin = new Thickness(8, 3, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Text = "硬盘总计：—　未分配：—　当前分区：—　当前分区剩余：—"
    };
    private readonly Button _downloadArt2Button = new() { Content = "一键下载ART2图集", Margin = new Thickness(4) };
    private readonly ListBox _entries = new() { SelectionMode = SelectionMode.Multiple };
    private readonly ContextMenu _entryMenu = new();
    private readonly MenuItem _copyMenuItem = new() { Header = "复制" };
    private readonly MenuItem _deleteMenuItem = new() { Header = "删除" };
    private readonly MenuItem _renameMenuItem = new() { Header = "重命名" };
    private readonly Separator _entryMenuSeparator = new();
    private readonly MenuItem _pasteMenuItem = new() { Header = "粘贴" };
    private readonly MenuItem _newDirectoryMenuItem = new() { Header = "新建目录" };
    private readonly Separator _entryMenuSeparator2 = new();
    private readonly MenuItem _newPartitionMenuItem = new() { Header = "新建 PFS 分区" };
    private readonly MenuItem _renamePartitionMenuItem = new() { Header = "重命名分区" };
    private readonly MenuItem _deletePartitionMenuItem = new() { Header = "删除 PFS 分区" };
    private readonly MenuItem _refreshMenuItem = new() { Header = "刷新" };

    private readonly TextBox _log = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("Menlo, Monaco, monospace"),
        FontSize = 12
    };

    private readonly TextBlock _statusLabel = new()
    {
        Text = "就绪",
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly ProgressBar _progressBar = new()
    {
        IsVisible = false,
        Minimum = 0,
        Maximum = 1000,
        Value = 0,
        Width = 220,
        Height = 16,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0)
    };
    private readonly TextBlock _transferSpeedLabel = new()
    {
        IsVisible = false,
        Width = 180,
        TextAlignment = TextAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0)
    };
    private readonly Button _cancelTransferButton = new()
    {
        Content = "取消传输",
        IsVisible = false,
        IsEnabled = false,
        Padding = new Thickness(8, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0)
    };
    private readonly TextBox _pfsNetIpBox = new()
    {
        Width = 190,
        Watermark = "PS2 PfsNet IP，例如 192.168.1.10",
        VerticalAlignment = VerticalAlignment.Center
    };

    private Panel _topContainer = null!;
    private TabControl _tabs = null!;

    private string _currentPath = "/";
    private string? _activePartition;
    private int _busyDepth;
    private int _fileTransferDepth;
    private bool _suppressShowSystemPartitionsEvent;
    private bool _suppressDeviceComboEvent;
    private CancellationTokenSource? _transferCts;
    private readonly object _sleepGuardLock = new();
    private Process? _caffeinateProcess;

    public MainWindow()
    {
        var toolsDirectory = Path.Combine(AppContext.BaseDirectory, "tools");
        _pfs = new PfsShellClient(toolsDirectory);
        _nbd = new NbdClient(toolsDirectory);
        _diskDiscovery = new DiskDiscovery(toolsDirectory);
        _apaSpaceReader = new ApaSpaceReader();
        _apaPartitionTableReader = new ApaPartitionTableReader();
        _pfsSpaceReader = new PfsSpaceReader();

        Title = "PS2 HDD File Manager v1.0 — 峰哥原创，免费发布";
        MinWidth = 960;
        MinHeight = 660;
        Width = 1008;
        Height = 901;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("PingFang SC, Heiti SC, STHeiti, Inter, sans-serif");

        BuildUi();

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Opened += async (_, _) => await RefreshDisksAsync();
        Closed += (_, _) =>
        {
            StopMacSleepGuard();
            try { _pfsNet.Disconnect(); } catch { }
            _pfsNet.Dispose();
            _nbd.Dispose();
        };
    }

    private void BuildUi()
    {
        _entries.ItemTemplate = new FuncDataTemplate<BrowserRowItem>((row, _) =>
        {
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("310,120,*"),
                Margin = new Thickness(4, 2)
            };
            if (row is null)
                return grid;

            var nameBlock = new TextBlock
            {
                Text = row.DisplayName,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 8, 0)
            };
            var typeBlock = new TextBlock
            {
                Text = row.TypeText,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 8, 0)
            };
            var detailsBlock = new TextBlock
            {
                Text = row.DetailsText,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            Grid.SetColumn(nameBlock, 0);
            Grid.SetColumn(typeBlock, 1);
            Grid.SetColumn(detailsBlock, 2);
            grid.Children.Add(nameBlock);
            grid.Children.Add(typeBlock);
            grid.Children.Add(detailsBlock);
            return grid;
        });

        _entries.DoubleTapped += async (_, _) => await OpenSelectedAsync();
        _entries.AddHandler(PointerPressedEvent, OnEntriesPointerPressed, RoutingStrategies.Tunnel);
        _entries.KeyDown += OnEntriesKeyDown;

        DragDrop.SetAllowDrop(_entries, true);
        _entries.AddHandler(DragDrop.DragOverEvent, OnEntriesDragOver);
        _entries.AddHandler(DragDrop.DropEvent, OnEntriesDrop);

        _copyMenuItem.Click += async (_, _) => await CopySelectedToClipboardAsync();
        _deleteMenuItem.Click += async (_, _) => await DeleteSelectedAsync();
        _renameMenuItem.Click += async (_, _) => await RenameSelectedAsync();
        _pasteMenuItem.Click += async (_, _) => await PasteImportAsync();
        _newDirectoryMenuItem.Click += async (_, _) => await CreateDirectoryAsync();
        _newPartitionMenuItem.Click += async (_, _) => await CreatePfsPartitionAsync();
        _renamePartitionMenuItem.Click += async (_, _) => await RenameSelectedPartitionAsync();
        _deletePartitionMenuItem.Click += async (_, _) => await DeleteSelectedPartitionAsync();
        _refreshMenuItem.Click += async (_, _) => await RefreshBrowserAsync();

        _entryMenu.Items.Add(_copyMenuItem);
        _entryMenu.Items.Add(_deleteMenuItem);
        _entryMenu.Items.Add(_renameMenuItem);
        _entryMenu.Items.Add(_entryMenuSeparator);
        _entryMenu.Items.Add(_pasteMenuItem);
        _entryMenu.Items.Add(_newDirectoryMenuItem);
        _entryMenu.Items.Add(_entryMenuSeparator2);
        _entryMenu.Items.Add(_newPartitionMenuItem);
        _entryMenu.Items.Add(_renamePartitionMenuItem);
        _entryMenu.Items.Add(_deletePartitionMenuItem);
        _entryMenu.Items.Add(_refreshMenuItem);
        _entryMenu.Opening += OnEntryMenuOpening;
        _entries.ContextMenu = _entryMenu;

        _deviceCombo.SelectionChanged += async (_, _) =>
        {
            if (_suppressDeviceComboEvent)
                return;
            await LoadPartitionsAsync();
        };
        _refreshDisksButton.Click += async (_, _) => await RefreshDisksAsync();

        var sourceBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        sourceBar.Children.Add(new TextBlock { Text = "来源：", VerticalAlignment = VerticalAlignment.Center });
        sourceBar.Children.Add(_deviceCombo);
        sourceBar.Children.Add(_refreshDisksButton);
        sourceBar.Children.Add(MakeButton("加载硬盘镜像", async () => await SelectImageAsync()));

        var ejectBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        ejectBar.Children.Add(MakeButton("安全弹出硬盘", async () => await SafeEjectSelectedAsync()));

        var art2Bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        _downloadArt2Button.Click += async (_, _) => await DownloadArt2Async();
        art2Bar.Children.Add(_downloadArt2Button);

        // NBD UI 暂时隐藏，代码保留待后续开发
        var nbdBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, IsVisible = false };
        var ipBox = new TextBox { Width = 170, Watermark = "PS2 NBD IP，例如 192.168.1.10" };
        nbdBar.Children.Add(new TextBlock { Text = "网络(NBD)：", VerticalAlignment = VerticalAlignment.Center });
        nbdBar.Children.Add(ipBox);
        nbdBar.Children.Add(MakeButton("连接 NBD", async () => await ConnectNbdAsync(ipBox.Text ?? string.Empty)));
        nbdBar.Children.Add(MakeButton("断开 NBD", async () => await DisconnectNbdAsync()));

        var pfsNetBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var lastPfsNetIp = LoadLastPfsNetIp();
        if (!string.IsNullOrWhiteSpace(lastPfsNetIp))
            _pfsNetIpBox.Text = lastPfsNetIp;
        pfsNetBar.Children.Add(new TextBlock { Text = "PFS 网络：", VerticalAlignment = VerticalAlignment.Center });
        pfsNetBar.Children.Add(_pfsNetIpBox);
        pfsNetBar.Children.Add(MakeButton("连接 PFS 网络", async () => await ConnectPfsNetAsync(_pfsNetIpBox.Text ?? string.Empty)));
        pfsNetBar.Children.Add(MakeButton("断开 PFS 网络", async () => await DisconnectPfsNetAsync()));

        var actionBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        actionBar.Children.Add(MakeButton("上级目录", async () => await GoUpAsync()));
        actionBar.Children.Add(_showSystemPartitionsCheckBox);
        actionBar.Children.Add(_pathBox);
        _showSystemPartitionsCheckBox.IsCheckedChanged += async (_, _) => await OnShowSystemPartitionsChangedAsync();

        var diskSpaceBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        diskSpaceBar.Children.Add(_diskSpaceLabel);

        var top = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Margin = new Thickness(10)
        };
        top.Children.Add(sourceBar);
        top.Children.Add(nbdBar);
        top.Children.Add(pfsNetBar);
        top.Children.Add(ejectBar);
        top.Children.Add(art2Bar);
        top.Children.Add(diskSpaceBar);
        top.Children.Add(actionBar);
        _topContainer = top;

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("310,120,*"),
            Background = new SolidColorBrush(Color.FromRgb(242, 242, 242)),
            Margin = new Thickness(0, 0, 0, 1)
        };
        var h0 = new TextBlock { Text = "名称", FontWeight = FontWeight.SemiBold, Margin = new Thickness(12, 6) };
        var h1 = new TextBlock { Text = "类型", FontWeight = FontWeight.SemiBold, Margin = new Thickness(4, 6) };
        var h2 = new TextBlock { Text = "pfsshell 详情", FontWeight = FontWeight.SemiBold, Margin = new Thickness(4, 6) };
        Grid.SetColumn(h0, 0);
        Grid.SetColumn(h1, 1);
        Grid.SetColumn(h2, 2);
        headerGrid.Children.Add(h0);
        headerGrid.Children.Add(h1);
        headerGrid.Children.Add(h2);

        var browserLayout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(headerGrid, Dock.Top);
        browserLayout.Children.Add(headerGrid);
        browserLayout.Children.Add(_entries);

        _tabs = new TabControl { Margin = new Thickness(6, 0, 6, 4) };
        _tabs.Items.Add(new TabItem { Header = "PFS 浏览器", Content = browserLayout });
        _tabs.Items.Add(new TabItem { Header = "诊断输出", Content = _log });

        _cancelTransferButton.Click += (_, _) => RequestCancelTransfer();
        var statusBar = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(215, 215, 215)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(246, 246, 246)),
            Padding = new Thickness(10, 4)
        };
        var statusDock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_transferSpeedLabel, Dock.Right);
        DockPanel.SetDock(_progressBar, Dock.Right);
        DockPanel.SetDock(_cancelTransferButton, Dock.Right);
        statusDock.Children.Add(_transferSpeedLabel);
        statusDock.Children.Add(_progressBar);
        statusDock.Children.Add(_cancelTransferButton);
        statusDock.Children.Add(_statusLabel);
        statusBar.Child = statusDock;

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(statusBar);
        root.Children.Add(_tabs);
        Content = root;
    }

    private static Button MakeButton(string text, Func<Task> onClick)
    {
        var button = new Button { Content = text, Margin = new Thickness(4) };
        button.Click += async (_, _) => await onClick();
        return button;
    }

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _transferCts is not null)
        {
            e.Handled = true;
            RequestCancelTransfer();
            return;
        }

        if (e.Key != Key.Back || IsEditableTextInputFocused())
            return;

        // Cmd+Backspace 在列表内按删除处理，不触发返回上级
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            return;

        e.Handled = true;
        await GoUpAsync();
    }

    private void OnEntriesPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(_entries).Properties;
        if (!props.IsRightButtonPressed)
            return;

        if ((e.Source as StyledElement)?.DataContext is BrowserRowItem row)
        {
            if (_entries.SelectedItems is not null && !_entries.SelectedItems.Contains(row))
            {
                _entries.SelectedItems.Clear();
                _entries.SelectedItems.Add(row);
            }
        }
        else
        {
            _entries.SelectedItems?.Clear();
        }
    }

    private async void OnEntriesKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            e.Handled = true;
            await RenameSelectedAsync();
            return;
        }

        if (e.Key == Key.Delete || (e.Key == Key.Back && e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            e.Handled = true;
            if (_activePartition is null)
                await DeleteSelectedPartitionAsync();
            else
                await DeleteSelectedAsync();
            return;
        }

        var cmdOrCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (!cmdOrCtrl)
            return;

        if (e.Key == Key.A)
        {
            e.Handled = true;
            _entries.SelectAll();
            return;
        }

        if (e.Key == Key.C)
        {
            e.Handled = true;
            await CopySelectedToClipboardAsync();
            return;
        }

        if (e.Key == Key.V)
        {
            e.Handled = true;
            await PasteImportAsync();
        }
    }

    private void OnEntriesDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private async void OnEntriesDrop(object? sender, DragEventArgs e)
    {
        if (_activePartition is null)
        {
            await ShowWarningAsync("请先双击打开一个 PFS 分区，再拖入文件。");
            return;
        }

        var files = e.Data.GetFiles();
        if (files is null)
            return;

        var paths = files
            .Select(item => item.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
            .Cast<string>()
            .ToArray();
        if (paths.Length == 0)
            return;

        await ImportLocalItemsAsync(paths);
    }

    private void OnEntryMenuOpening(object? sender, CancelEventArgs e)
    {
        var selected = SelectedEntries();
        var inPartition = _activePartition is not null;
        var onFileItem = inPartition && selected.Count > 0;

        _copyMenuItem.IsVisible = onFileItem;
        _deleteMenuItem.IsVisible = onFileItem;
        _renameMenuItem.IsVisible = onFileItem;
        _entryMenuSeparator.IsVisible = onFileItem;
        _copyMenuItem.IsEnabled = onFileItem;
        _deleteMenuItem.IsEnabled = onFileItem;
        _renameMenuItem.IsEnabled = selected.Count == 1;

        _pasteMenuItem.IsVisible = inPartition;
        _newDirectoryMenuItem.IsVisible = inPartition;
        _pasteMenuItem.IsEnabled = inPartition;
        _newDirectoryMenuItem.IsEnabled = inPartition;
        _entryMenuSeparator2.IsVisible = inPartition;

        var hasDevice = (_deviceCombo.SelectedItem as DeviceItem)?.Path is { Length: > 0 };
        _newPartitionMenuItem.IsVisible = !inPartition;
        _newPartitionMenuItem.IsEnabled = !inPartition && hasDevice;
        _renamePartitionMenuItem.IsVisible = !inPartition;
        _renamePartitionMenuItem.IsEnabled = !inPartition && selected.Count == 1 && !IsProtectedPartitionName(selected[0].Name) && hasDevice;
        _deletePartitionMenuItem.IsVisible = !inPartition;
        _deletePartitionMenuItem.IsEnabled = !inPartition && selected.Count == 1 && !IsProtectedPartitionName(selected[0].Name) && hasDevice;
    }

    private async Task DownloadArt2Async()
    {
        AppendLog("「一键下载ART2图集」尚未实现：后续将下载到当前分区的 ART2 文件夹。");
        await ShowWarningAsync("该功能尚未实现。\n\n预留说明：图集将下载到当前 PFS 分区内的 ART2 文件夹。");
    }

    private async Task RefreshDisksAsync()
    {
        await RunBusyAsync("正在扫描 PS2 APA 硬盘…", async () =>
        {
            var disks = await _diskDiscovery.GetPs2ApaDisksAsync(CancellationToken.None);
            _suppressDeviceComboEvent = true;
            try
            {
                _deviceCombo.Items.Clear();
                ResetPfsExplorer();
                foreach (var disk in disks)
                    AddSourceDevice(disk.DevicePath, disk.DisplayName);
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
            }
            finally
            {
                _suppressDeviceComboEvent = false;
            }
        });

        if ((_deviceCombo.SelectedItem as DeviceItem)?.Path is { Length: > 0 })
            await LoadPartitionsAsync();
    }

    private async Task SelectImageAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 PS2 HDD 原始镜像",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("磁盘镜像") { Patterns = ["*.img", "*.bin", "*.raw"] },
                new FilePickerFileType("所有文件") { Patterns = ["*.*"] }
            ]
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        SelectImageDevice(path, $"镜像 · {path}");
        ResetPfsExplorer();
        AppendLog($"已选择镜像：{path}");
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
        if (await RejectNetworkPartitionMutationIfNeededAsync(device)) return;

        var isNet = PfsNetProtocol.IsNetworkDevice(device);
        ApaPartitionTable layout;
        try
        {
            layout = await ReadApaLayoutAsync(device, CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppendLog($"拒绝创建分区：无法通过 APA 校验读取分区表。{ex.Message}");
            await ShowWarningAsync("分区表无法通过完整校验，已拒绝写入。请先备份硬盘并修复分区表。");
            return;
        }

        var request = await PartitionCreatePrompt.ShowAsync(this, FormatBytes(layout.FreeBytes), FormatPartitionSize(layout.FreeBytes));
        if (request is null) return;
        if (!TryParsePartitionSize(request.Size, out var requestedBytes, out var normalizedSize, out var sizeError))
        {
            await ShowWarningAsync(sizeError);
            return;
        }
        if (!IsSafePartitionName(request.Name, out var nameError))
        {
            await ShowWarningAsync(nameError);
            return;
        }
        if (layout.Partitions.Any(partition => !partition.IsSubPartition && partition.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase)))
        {
            await ShowWarningAsync($"分区“{request.Name}”已存在。");
            return;
        }
        if (requestedBytes > layout.FreeBytes)
        {
            await ShowWarningAsync($"APA 未分配空间不足。请求 {FormatBytes(requestedBytes)}，当前只有 {FormatBytes(layout.FreeBytes)}。未执行任何写入。");
            return;
        }

        var sourceDisplay = (_deviceCombo.SelectedItem as DeviceItem)?.Display ?? device;
        if (!await PartitionCreateConfirmation.ShowAsync(this, sourceDisplay, request.Name, FormatBytes(requestedBytes)))
            return;

        var created = false;
        await RunBusyAsync($"正在创建 PFS 分区“{request.Name}” ({normalizedSize})…", async () =>
        {
            var currentLayout = await ReadApaLayoutAsync(device, CancellationToken.None);
            if (currentLayout.Partitions.Any(partition => !partition.IsSubPartition && partition.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"分区“{request.Name}”已存在，已取消创建。");
            if (requestedBytes > currentLayout.FreeBytes)
                throw new InvalidOperationException("APA 未分配空间已变化且不足，已取消创建。");

            if (isNet)
                await _pfsNet.ApaCreatePfsAsync(request.Name, normalizedSize, CancellationToken.None);
            else
            {
                var create = await _pfs.CreatePfsPartitionAsync(device, request.Name, normalizedSize, CancellationToken.None);
                AppendResult(create);
                if (!PfsShellClient.IsSuccessfulSession(create))
                    throw new InvalidOperationException("pfsshell 未能创建 PFS 分区，请查看诊断输出。");
            }

            var afterCreate = await ReadApaLayoutAsync(device, CancellationToken.None);
            var createdPartition = afterCreate.Partitions.SingleOrDefault(partition =>
                !partition.IsSubPartition && partition.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase));
            var hasExactSize = createdPartition is not null && createdPartition.Type == 0x0100 && createdPartition.TotalBytes == requestedBytes;

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
        if (await RejectNetworkPartitionMutationIfNeededAsync(device))
            return;
        if (SelectedEntry() is not { } selected)
        {
            await ShowWarningAsync("请在分区列表中选择一个要删除的 PFS 分区。");
            return;
        }

        var name = selected.Name;
        if (IsProtectedPartitionName(name))
        {
            AppendLog($"拒绝删除系统保留分区“{name}”。");
            await ShowWarningAsync($"“{name}”是系统保留分区，程序禁止删除。");
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
            await ShowWarningAsync("分区表无法通过完整校验，已拒绝删除。请先备份硬盘并修复分区表。");
            return;
        }

        if (target is null || target.Type != 0x0100)
        {
            AppendLog($"拒绝删除分区“{name}”：目标不是有效的 PFS 主分区。");
            await ShowWarningAsync("目标不是可安全删除的 PFS 主分区，已拒绝操作。");
            return;
        }

        var sourceDisplay = (_deviceCombo.SelectedItem as DeviceItem)?.Display ?? device;
        var size = FormatBytes(target.TotalBytes);
        if (!await MessageDialog.ConfirmYesNoAsync(
                this,
                "第一次确认：删除 PFS 分区",
                $"将永久删除 PFS 分区“{name}”（{size}）及其全部文件。\n\n来源：{sourceDisplay}\n\n此操作会修改 APA 分区表，删除后无法恢复。确定继续？"))
            return;

        var secondConfirm = isNet
            ? $"最后确认：立即删除“{name}”？\n\n程序将通过 PfsNet 删除分区，并在删除后重新校验 APA 分区表。"
            : $"最后确认：立即删除“{name}”？\n\n程序将使用 pfsshell 的标准 rmpart 操作，并在删除后重新校验 APA 分区表。";
        if (!await MessageDialog.ConfirmYesNoAsync(
                this,
                "第二次确认：永久删除",
                secondConfirm,
                defaultNo: true))
            return;

        var deleted = false;
        await RunBusyAsync($"正在删除 PFS 分区“{name}”…", async () =>
        {
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
        if (await RejectNetworkPartitionMutationIfNeededAsync(device))
            return;
        if (SelectedEntry() is not { } selected)
        {
            await ShowWarningAsync("请在分区列表中选择一个要重命名的 PFS 分区。");
            return;
        }

        var oldName = selected.Name;
        if (IsProtectedPartitionName(oldName))
        {
            await ShowWarningAsync($"“{oldName}”是系统保留分区，程序禁止重命名。");
            return;
        }

        var newName = await Prompt.ShowAsync(this, "重命名 PFS 分区", "新分区名称：", oldName);
        if (newName is null || newName.Equals(oldName, StringComparison.Ordinal))
            return;
        if (!IsSafePartitionName(newName, out var nameError))
        {
            await ShowWarningAsync(nameError);
            return;
        }
        if (IsProtectedPartitionName(newName))
        {
            await ShowWarningAsync("不能使用系统保留分区名称。");
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
                await ShowWarningAsync("目标不是有效的 PFS 主分区，已取消重命名。");
                return;
            }
            if (layout.Partitions.Any(partition => !partition.IsSubPartition && partition.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
            {
                await ShowWarningAsync($"分区“{newName}”已存在。");
                return;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"拒绝重命名分区：无法通过 APA 校验读取分区表。{ex.Message}");
            await ShowWarningAsync("分区表无法通过完整校验，已拒绝重命名。请先备份硬盘并修复分区表。");
            return;
        }

        var sourceDisplay = (_deviceCombo.SelectedItem as DeviceItem)?.Display ?? device;
        if (!await MessageDialog.ConfirmYesNoAsync(
                this,
                "确认重命名 PFS 分区",
                $"将把 PFS 分区“{oldName}”重命名为“{newName}”。\n\n来源：{sourceDisplay}\n\n此操作只修改 APA 分区表中的名称，不移动分区或文件数据。请勿断电、拔出硬盘或强制关闭程序。确定继续？",
                defaultNo: true))
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
        _suppressDeviceComboEvent = true;
        try
        {
            AddSourceDevice(imagePath, displayName);
            _deviceCombo.SelectedIndex = _deviceCombo.Items.Count - 1;
        }
        finally
        {
            _suppressDeviceComboEvent = false;
        }
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
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            await ShowWarningAsync("请输入 PS2 的 NBD IP 地址。");
            return;
        }
        if (_pfsNet.IsConnected)
        {
            await ShowWarningAsync("已连接 PFS 网络。请先断开 PFS 网络后再连接 NBD（二者互斥）。");
            return;
        }

        string? mappedDevicePath = null;
        await RunBusyAsync("正在连接 NBD…", async () =>
        {
            var result = await _nbd.MapAsync(ipAddress.Trim(), CancellationToken.None);
            AppendResult(result);
            if (result.ExitCode != 0) throw new InvalidOperationException("WNBD 映射失败，请查看诊断输出。");

            var diskNumber = await _nbd.GetMappedDiskNumberAsync(CancellationToken.None);
            mappedDevicePath = $@"\\.\PhysicalDrive{diskNumber}";
            _suppressDeviceComboEvent = true;
            try
            {
                AddSourceDevice(mappedDevicePath, $"PS2 NBD · {ipAddress.Trim()} · PhysicalDrive{diskNumber}");
                _deviceCombo.SelectedIndex = _deviceCombo.Items.Count - 1;
            }
            finally
            {
                _suppressDeviceComboEvent = false;
            }
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
            await ShowWarningAsync("请输入运行 ps2-server 的 PS2 IP 地址。");
            return;
        }

        if (_nbd.IsMapRunning)
        {
            await ShowWarningAsync("NBD 映射仍在运行。请先断开 NBD 后再连接 PFS 网络（二者互斥）。");
            return;
        }

        SaveLastPfsNetIp(ipAddress.Trim());
        await RunBusyAsync("正在连接 PfsNet…", async () =>
        {
            await _pfsNet.ConnectAsync(ipAddress.Trim(), CancellationToken.None);
            var path = PfsNetProtocol.MakeDevicePath(ipAddress.Trim());
            _suppressDeviceComboEvent = true;
            try
            {
                AddSourceDevice(path, $"PS2 PfsNet · {ipAddress.Trim()}");
                _deviceCombo.SelectedIndex = _deviceCombo.Items.Count - 1;
            }
            finally
            {
                _suppressDeviceComboEvent = false;
            }
            ResetPfsExplorer();
            AppendLog($"已连接 PfsNet（{ipAddress.Trim()}:{PfsNetProtocol.CommandPort}）。数据口：本机 TCP {PfsNetProtocol.DataPort}。features=0x{_pfsNet.Features:X}。");
            if ((_pfsNet.Features & PfsNetProtocol.FeatStreamRing) == 0)
            {
                AppendLog("提示：检测到 PS2 端 PfsNetServer.elf 为旧版（未开启 8MB 环形缓冲与高速线程调度），请同步替换 PS2 上的 PfsNetServer.elf 以获得完整传输速度与防断线优化。");
            }
        });

        if (_pfsNet.IsConnected)
            await LoadPartitionsAsync();
    }

    private async Task DisconnectPfsNetAsync()
    {
        await RunBusyAsync("正在断开 PfsNet…", () =>
        {
            var host = _pfsNet.Host;
            _pfsNet.Disconnect();
            _suppressDeviceComboEvent = true;
            try
            {
                if (host is not null)
                {
                    var path = PfsNetProtocol.MakeDevicePath(host);
                    for (var i = _deviceCombo.Items.Count - 1; i >= 0; i--)
                    {
                        if (_deviceCombo.Items[i] is DeviceItem item && item.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
                            _deviceCombo.Items.RemoveAt(i);
                    }
                }
            }
            finally
            {
                _suppressDeviceComboEvent = false;
            }

            ResetPfsExplorer();
            AppendLog("已断开 PfsNet。");
            return Task.CompletedTask;
        });

        _suppressDeviceComboEvent = true;
        try
        {
            if (_deviceCombo.Items.Count == 0)
            {
                _deviceCombo.Items.Add(new DeviceItem(string.Empty, "未检测到PS2硬盘"));
                _deviceCombo.SelectedIndex = 0;
            }
            else if (_deviceCombo.SelectedItem is null)
            {
                _deviceCombo.SelectedIndex = 0;
            }
        }
        finally
        {
            _suppressDeviceComboEvent = false;
        }

        if ((_deviceCombo.SelectedItem as DeviceItem)?.Path is { Length: > 0 })
            await LoadPartitionsAsync();
    }

    private async Task<bool> RejectNetworkPartitionMutationIfNeededAsync(string device)
    {
        if (!PfsNetProtocol.IsNetworkDevice(device))
            return false;
        if (_pfsNet.SupportsApaMutate)
            return false;
        await ShowWarningAsync("网络模式分区管理尚未启用。\n\n需待 APA 扇区校验（A1）与分区变更命令就绪后可用。当前仅支持文件/目录浏览与后续文件操作。");
        return true;
    }

    private async Task SafeEjectSelectedAsync()
    {
        if (Volatile.Read(ref _fileTransferDepth) != 0)
        {
            await ShowWarningAsync("正在传输文件，请等待完成后再安全弹出。");
            return;
        }

        if (_deviceCombo.SelectedItem is not DeviceItem item || string.IsNullOrWhiteSpace(item.Path))
        {
            await ShowWarningAsync("请先选择要弹出的 PS2 硬盘。");
            return;
        }

        if (PfsNetProtocol.IsNetworkDevice(item.Path))
        {
            if (!await MessageDialog.ConfirmYesNoAsync(
                    this,
                    Title ?? "PS2 HDD File Manager",
                    "当前来源是 PFS 网络连接，无法硬件弹出。\n是否断开 PfsNet？"))
                return;
            await DisconnectPfsNetAsync();
            return;
        }

        if (DiskSafeEject.TryParseMacDiskPath(item.Path, out var macDisk))
        {
            if (!await MessageDialog.ConfirmYesNoAsync(
                    this,
                    Title ?? "PS2 HDD File Manager",
                    $"确定要安全弹出吗？\n\n{item.Display}\n\n弹出前请确认没有其它程序正在访问该硬盘。"))
                return;

            var ejectedMac = false;
            await RunBusyAsync($"正在安全弹出 {macDisk}…", async () =>
            {
                await DiskSafeEject.EjectMacDiskAsync(macDisk, CancellationToken.None);
                ejectedMac = true;
                AppendLog($"已安全弹出 {macDisk}。");
            });
            if (!ejectedMac)
                return;

            RemoveSelectedSourceDevice();
            ResetPfsExplorer();
            AppendLog("硬盘已从列表移除。请确认硬件指示灯熄灭后拔出。");
            return;
        }

        if (!DiskSafeEject.TryParsePhysicalDriveNumber(item.Path, out var driveNumber))
        {
            if (!await MessageDialog.ConfirmYesNoAsync(
                    this,
                    Title ?? "PS2 HDD File Manager",
                    $"“{item.Display}”是镜像文件，无法执行硬件弹出。\n是否仅从列表中移除？"))
                return;

            RemoveSelectedSourceDevice();
            ResetPfsExplorer();
            AppendLog($"已从列表移除镜像：{item.Path}");
            return;
        }

        var isNbd = item.Display.StartsWith("PS2 NBD", StringComparison.OrdinalIgnoreCase);
        var action = isNbd ? "断开 NBD 映射" : "安全弹出";
        if (!await MessageDialog.ConfirmYesNoAsync(
                this,
                Title ?? "PS2 HDD File Manager",
                $"确定要{action}吗？\n\n{item.Display}\n\n弹出前请确认没有其它程序正在访问该硬盘。"))
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
            await ShowInfoAsync(DiskSafeEject.NonHotPlugReadyMessage(driveNumber) + "\n\n硬盘已从列表移除。");
            return;
        }

        AppendLog("硬盘已从列表移除。若托盘仍提示可安全移除，请再确认硬件指示灯熄灭后拔出。");
    }

    private void RemoveSelectedSourceDevice()
    {
        var index = _deviceCombo.SelectedIndex;
        if (index < 0)
            return;
        _suppressDeviceComboEvent = true;
        try
        {
            _deviceCombo.Items.RemoveAt(index);
            if (_deviceCombo.Items.Count == 0)
            {
                _deviceCombo.Items.Add(new DeviceItem(string.Empty, "未检测到PS2硬盘"));
                _deviceCombo.SelectedIndex = 0;
                return;
            }

            _deviceCombo.SelectedIndex = Math.Min(index, _deviceCombo.Items.Count - 1);
        }
        finally
        {
            _suppressDeviceComboEvent = false;
        }
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
                    _showSystemPartitionsCheckBox.IsChecked == true,
                    CancellationToken.None);
                partitions = infos
                    .Where(p => p.IsPfs)
                    .Select(p => p.Name)
                    .Where(name => _showSystemPartitionsCheckBox.IsChecked == true || !IsProtectedPartitionName(name))
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
                if (_showSystemPartitionsCheckBox.IsChecked != true)
                    partitions = partitions.Where(name => !IsProtectedPartitionName(name)).ToList();
            }

            _activePartition = null;
            await UpdateDiskSpaceAsync(device, null);
            _currentPath = "/";
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

                var details = isSystem
                    ? "系统保留分区；可浏览读写，禁止删除/重命名分区"
                    : "可操作的 PFS 分区；双击查看";
                var entry = new PfsEntry(partition, true, details);
                var typeText = isSystem ? $"系统分区{sizeHint}" : $"PFS 分区{sizeHint}";
                _entries.Items.Add(new BrowserRowItem(partition, typeText, details, entry));
            }
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

        if (_showSystemPartitionsCheckBox.IsChecked == true)
        {
            var confirmed = await MessageDialog.ConfirmOkCancelAsync(
                this,
                Title ?? "PS2 HDD File Manager",
                "即将显示系统保留分区（如 __system、__common 等）。\n\n" +
                "确认后可以浏览并自由读写这些分区内的文件，但禁止删除或重命名系统分区本身。\n" +
                "误操作可能导致 PS2 无法正常启动。\n\n是否继续？");
            if (!confirmed)
            {
                _suppressShowSystemPartitionsEvent = true;
                _showSystemPartitionsCheckBox.IsChecked = false;
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

            _entries.Items.Clear();
            foreach (var entry in entries)
            {
                var displayName = PfsNameEncoding.ToDisplay(entry.Name);
                var typeText = entry.IsDirectory ? "目录" : "文件";
                var detailsText = PfsNameEncoding.ToDisplay(entry.Details);
                _entries.Items.Add(new BrowserRowItem(displayName, typeText, detailsText, entry));
            }
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

    private bool IsEditableTextInputFocused()
    {
        if (FocusManager?.GetFocusedElement() is TextBox textBox)
            return !textBox.IsReadOnly;
        return false;
    }

    private async Task ExportSelectedAsync()
    {
        if (SelectedEntry() is not { IsDirectory: false } entry)
        {
            await ShowWarningAsync("请选择一个文件。目录导出将在后续版本加入。");
            return;
        }
        if (!TryGetPfsTarget(out var device, out var partition)) return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择导出到的本地目录",
            AllowMultiple = false
        });
        var selectedPath = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(selectedPath))
            return;

        BeginFileTransfer();
        var ct = BeginTransferCancellation();
        try
        {
            await RunBusyAsync($"正在导出 {PfsNameEncoding.ToDisplay(entry.Name)}…", async () =>
            {
                if (PfsNetProtocol.IsNetworkDevice(device))
                {
                    await _pfsNet.MountAsync(partition, CancellationToken.None);
                    var localPath = Path.Combine(selectedPath, PfsNameEncoding.ToDisplay(entry.Name));
                    await _pfsNet.GetFileAsync(_currentPath, entry.Name, localPath, ct);
                    AppendLog($"已通过 PfsNet 导出：{localPath}");
                    return;
                }

                var result = await _pfs.ExportAsync(device, partition, _currentPath, entry.Name, selectedPath, ct);
                AppendResult(result);
                if (!PfsShellClient.IsSuccessfulSession(result))
                    throw new InvalidOperationException("导出失败，请查看诊断输出。");
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
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入文件到当前 PFS 目录",
            AllowMultiple = true
        });
        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Cast<string>()
            .ToArray();
        if (paths.Length == 0)
            return;
        await ImportLocalItemsAsync(paths);
    }

    private async Task ImportFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "导入目录到当前 PFS 目录",
            AllowMultiple = true
        });
        var paths = folders
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Cast<string>()
            .ToArray();
        if (paths.Length == 0)
            return;
        await ImportLocalItemsAsync(paths);
    }

    private async Task PasteImportAsync()
    {
        var items = await GetClipboardFilePathsAsync();
        if (items.Length == 0)
        {
            await ShowWarningAsync("剪贴板中没有可导入的文件或目录。请先在 Finder 中复制文件或文件夹，或直接拖入窗口。");
            return;
        }

        AppendLog($"从剪贴板解析到 {items.Length} 个项目。");
        await ImportLocalItemsAsync(items);
    }

    private async Task<string[]> GetClipboardFilePathsAsync()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var paths = new List<string>();

        void AddCandidate(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return;
            var trimmed = candidate.Trim();
            if ((File.Exists(trimmed) || Directory.Exists(trimmed)) && seen.Add(trimmed))
                paths.Add(trimmed);
        }

        try
        {
            if (Clipboard is not null)
            {
                var data = await Clipboard.GetDataAsync(DataFormats.Files);
                if (data is IEnumerable<IStorageItem> storageItems)
                {
                    foreach (var item in storageItems)
                        AddCandidate(item.TryGetLocalPath());
                }
                else if (data is IEnumerable<string> strItems)
                {
                    foreach (var item in strItems)
                        AddCandidate(item);
                }

                if (paths.Count == 0)
                {
                    var text = await Clipboard.GetTextAsync();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        foreach (var line in text.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                            AddCandidate(line);
                    }
                }
            }
        }
        catch
        {
            // 忽略并走 macOS Finder 剪贴板兜底
        }

        if (paths.Count == 0 && OperatingSystem.IsMacOS() && File.Exists("/usr/bin/osascript"))
        {
            try
            {
                const string jxa = "ObjC.import('AppKit'); var pb = $.NSPasteboard.generalPasteboard; var urls = pb.readObjectsForClassesOptions($([$.NSURL.class]), $({})); var out = []; if (urls) { var n = urls.count; for (var i = 0; i < n; i++) { var p = ObjC.unwrap(urls.objectAtIndex(i).path); if (p) out.push(p); } } out.join('\\n');";
                var res = await ProcessRunner.RunAsync(
                    "/usr/bin/osascript",
                    $"-l JavaScript -e \"{jxa.Replace("\"", "\\\"")}\"");
                if (res.ExitCode == 0 && !string.IsNullOrWhiteSpace(res.StandardOutput))
                {
                    foreach (var line in res.StandardOutput.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        AddCandidate(line);
                }
            }
            catch
            {
                // 忽略
            }
        }

        return paths.ToArray();
    }

    private async Task CopySelectedToClipboardAsync()
    {
        var selected = SelectedEntries();
        if (_activePartition is null || selected.Count == 0)
        {
            await ShowWarningAsync("请先打开 PFS 分区，并选择要复制的文件或目录。");
            return;
        }
        if (!TryGetPfsTarget(out var device, out var partition))
            return;

        var sourcePath = _currentPath;
        var sourceItems = selected
            .Select(entry => new PfsClipboardSelection(entry.Name, entry.IsDirectory, entry.Size))
            .ToArray();
        var exportProgress = new VirtualExportProgressState();
        var clipboardRoot = Path.Combine(AppDataDirectory, "Clipboard");
        var stagingDirectory = Path.Combine(
            clipboardRoot,
            Guid.NewGuid().ToString("N"));
        var succeeded = false;

        BeginFileTransfer();
        var ct = BeginTransferCancellation();
        try
        {
            await RunBusyAsync($"正在复制 {sourceItems.Length} 个项目到剪贴板…", async () =>
            {
                var files = await BuildVirtualClipboardFilesAsync(device, partition, sourcePath, sourceItems, exportProgress);
                try
                {
                    if (Directory.Exists(clipboardRoot))
                    {
                        foreach (var oldDir in Directory.GetDirectories(clipboardRoot))
                        {
                            try { Directory.Delete(oldDir, recursive: true); } catch { }
                        }
                    }
                }
                catch { }
                Directory.CreateDirectory(stagingDirectory);

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (file.IsDirectory)
                    {
                        Directory.CreateDirectory(Path.Combine(stagingDirectory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                        continue;
                    }

                    await MaterializeClipboardFileAsync(device, partition, file, stagingDirectory, exportProgress, ct);
                }

                var topLevelPaths = sourceItems
                    .Select(item => Path.Combine(stagingDirectory, PfsNameEncoding.ToDisplay(item.Name)))
                    .Where(p => File.Exists(p) || Directory.Exists(p))
                    .ToArray();

                await SetClipboardFilesAsync(topLevelPaths);
                succeeded = true;
                AppendLog($"已将 {topLevelPaths.Length} 个项目复制到剪贴板（可在 Finder 中直接粘贴，暂存目录：{stagingDirectory}）。");
            });
        }
        finally
        {
            if (!succeeded)
            {
                try
                {
                    if (Directory.Exists(stagingDirectory))
                        Directory.Delete(stagingDirectory, recursive: true);
                }
                catch { }
            }
            HideVirtualExportProgress();
            EndTransferCancellation();
            EndFileTransfer();
        }
    }

    private async Task SetClipboardFilesAsync(string[] paths)
    {
        if (paths.Length == 0)
            return;

        if (OperatingSystem.IsMacOS() && File.Exists("/usr/bin/osascript"))
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("ObjC.import('AppKit'); var pb = $.NSPasteboard.generalPasteboard; pb.clearContents; var arr = $.NSMutableArray.alloc.init; ");
                foreach (var path in paths)
                {
                    var escaped = path.Replace("\\", "\\\\").Replace("'", "\\'");
                    sb.Append($"arr.addObject($.NSURL.fileURLWithPath('{escaped}')); ");
                }
                sb.Append("pb.writeObjects(arr);");
                var script = sb.ToString().Replace("\"", "\\\"");
                var res = await ProcessRunner.RunAsync("/usr/bin/osascript", $"-l JavaScript -e \"{script}\"");
                if (res.ExitCode == 0)
                    return;
            }
            catch
            {
                // 失败则回退到 Avalonia 剪贴板
            }
        }

        if (Clipboard is not null)
        {
            await Clipboard.SetTextAsync(string.Join(Environment.NewLine, paths));
        }
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

            if (hasConflicts && !await OverwriteConfirmation.ShowAsync(this))
                return;

            ImportSpaceBudget? spaceBudget = null;
            await RunBusyAsync("正在检查当前 PFS 分区剩余空间…", async () =>
            {
                var currentSpace = await ReadPfsSpaceAsync(device, partition);
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

    private async Task CreateDirectoryAsync()
    {
        if (!TryGetPfsTarget(out var device, out var partition)) return;
        var name = await Prompt.ShowAsync(this, "新建目录", "目录名称：");
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

    private async Task RenameSelectedAsync()
    {
        if (_activePartition is null || SelectedEntry() is not { } entry)
        {
            await ShowWarningAsync("请先打开 PFS 分区，并选择要重命名的文件或目录。");
            return;
        }

        var currentDisplay = PfsNameEncoding.ToDisplay(entry.Name);
        var newName = await Prompt.ShowAsync(this, "重命名", "新名称：", currentDisplay);
        if (string.IsNullOrWhiteSpace(newName) || newName == currentDisplay)
            return;
        await ApplyRenameAsync(entry.Name, newName);
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
            await ShowWarningAsync("请先打开 PFS 分区，并选择一个或多个要删除的文件或目录。");
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
        if (!await MessageDialog.ConfirmYesNoAsync(this, "确认删除", message))
            return;

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
                    AppendLog($"[{idx + 1}/{pendingImports.Count}] 已导入 {remoteName}（{FormatByteSize((ulong)Math.Max(0, fileLength))}，用时 {fileSw.Elapsed:mm\\:ss}，{mbps:F2} MB/s）");
                }
            }

            batchSw.Stop();
            var batchSec = Math.Max(0.001, batchSw.Elapsed.TotalSeconds);
            var batchMbps = (batchTotalBytes / (1024.0 * 1024.0)) / batchSec;
            AppendLog($"已通过 PfsNet 导入 {pendingImports.Count} 个文件（共 {FormatByteSize((ulong)Math.Max(0, batchTotalBytes))}，总用时 {batchSw.Elapsed:hh\\:mm\\:ss}，平均 {batchMbps:F2} MB/s）。");
            return;
        }

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

            if (existing is null)
                return checked(required + EstimateLocalImportRequirement(localItem, zoneSizeBytes));

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

    private async Task<string> MaterializeClipboardFileAsync(
        string device,
        string partition,
        DeferredPfsFile file,
        string stagingDirectory,
        VirtualExportProgressState progress,
        CancellationToken ct)
    {
        var relativeDirectory = Path.GetDirectoryName(file.RelativePath.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        var destination = Path.Combine(stagingDirectory, relativeDirectory);
        Directory.CreateDirectory(destination);
        UpdateVirtualExportProgress(progress, file, 0, isCompleted: false);
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
        return localPath;
    }

    private static string LocalItemName(string path) =>
        Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private static string RemoteEntryKey(string device, string localName)
    {
        var pfsName = PfsNameEncoding.ToPfsFileName(localName);
        return PfsNetProtocol.IsNetworkDevice(device) ? pfsName : PfsNameEncoding.ToShell(pfsName);
    }

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
        if (!Regex.IsMatch(name, @"^[+A-Za-z0-9_.-]{1,32}$"))
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
        var match = Regex.Match(value.Trim(), @"^(?<value>\d+)\s*(?<unit>[mMgG])$");
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
        _progressBar.IsVisible = true;
        _transferSpeedLabel.Text = "0 MB/s";
        _transferSpeedLabel.IsVisible = true;
        _statusLabel.Text = progress.TotalFiles == 0
            ? "正在导入目录结构…"
            : $"准备导入：0 / {FormatBytes(progress.TotalBytes)} · {progress.TotalFiles} 个文件";
    }

    private void SetImportProgress(ImportProgressState progress, string localFilePath, long currentFileBytes, bool isCompleted)
    {
        if (isCompleted)
        {
            progress.CompletedFiles++;
            progress.CompletedBytes = Math.Min(progress.TotalBytes, progress.CompletedBytes + currentFileBytes);
        }

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
            var fraction = totalBytes > 0
                ? (double)displayedBytes / totalBytes
                : (totalFiles > 0 ? (double)completedFiles / totalFiles : 0d);
            _progressBar.Value = Math.Clamp(fraction * _progressBar.Maximum, 0, _progressBar.Maximum);
            _transferSpeedLabel.Text = FormatTransferSpeed(displayedBytes / elapsedSeconds);
            _statusLabel.Text = totalBytes > 0
                ? $"{action}：{FormatBytes(displayedBytes)} / {FormatBytes(totalBytes)} · {displayedFileNumber} / {totalFiles} 个文件 · {fileName}"
                : $"{action}：{displayedFileNumber} / {totalFiles} 个文件 · {fileName}";
        }

        PostOrRunUi(Update);
    }

    private void EndImportProgress(ImportProgressState progress)
    {
        _progressBar.IsVisible = false;
        _transferSpeedLabel.IsVisible = false;
        _statusLabel.Text = progress.CompletedFiles == progress.TotalFiles
            ? $"导入完成：{progress.CompletedFiles} 个文件 · {FormatBytes(progress.CompletedBytes)}"
            : $"导入结束：{progress.CompletedFiles} / {progress.TotalFiles} 个文件 · {FormatBytes(progress.CompletedBytes)}";
    }

    private bool TryGetDevice(out string device)
    {
        device = (_deviceCombo.SelectedItem as DeviceItem)?.Path ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(device)) return true;
        _ = ShowWarningAsync("未检测到 PS2 APA 硬盘。请连接硬盘后刷新，或手动选择镜像文件。");
        return false;
    }

    private bool TryGetPfsTarget(out string device, out string partition)
    {
        partition = _activePartition ?? string.Empty;
        if (!TryGetDevice(out device)) return false;
        if (!string.IsNullOrWhiteSpace(partition)) return true;
        _ = ShowWarningAsync("请先在浏览区双击打开一个 PFS 分区。");
        return false;
    }

    private PfsEntry? SelectedEntry() =>
        _entries.SelectedItems is { Count: 1 } list && list[0] is BrowserRowItem row ? row.Entry : null;

    private IReadOnlyList<PfsEntry> SelectedEntries() =>
        _entries.SelectedItems?
            .OfType<BrowserRowItem>()
            .Select(row => row.Entry)
            .ToList() ?? [];

    private static string CombineRemotePath(string parent, string child) =>
        (parent.TrimEnd('/') + "/" + child).Replace("//", "/");

    private void AppendResult(ProcessResult result) =>
        AppendLog(PfsNameEncoding.ToDisplay(result.CombinedOutput));

    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {PfsNameEncoding.TrimAscii(message)}{Environment.NewLine}";
        PostOrRunUi(() =>
        {
            _log.Text = (_log.Text ?? string.Empty) + line;
            _log.CaretIndex = _log.Text.Length;
        });
    }

    private Task ShowWarningAsync(string message) =>
        MessageDialog.ShowWarningAsync(this, Title ?? "PS2 HDD File Manager", message);

    private Task ShowInfoAsync(string message) =>
        MessageDialog.ShowInfoAsync(this, Title ?? "PS2 HDD File Manager", message);

    private void UpdateVirtualExportProgress(
        VirtualExportProgressState progress,
        DeferredPfsFile file,
        long currentBytes,
        bool isCompleted)
    {
        var snapshot = progress.Update(file, currentBytes, isCompleted);
        if (!snapshot.IsCompletedAll && !progress.ShouldPostUi(ProgressUiMinIntervalMs))
            return;

        void Update()
        {
            _progressBar.Minimum = 0;
            _progressBar.Maximum = 1000;
            _progressBar.Value = snapshot.TotalBytes > 0
                ? Math.Clamp((double)snapshot.DisplayedBytes / snapshot.TotalBytes * 1000, 0, 1000)
                : (snapshot.TotalFiles <= 0
                    ? 0
                    : Math.Clamp((double)snapshot.CompletedFiles / snapshot.TotalFiles * 1000, 0, 1000));
            _progressBar.IsVisible = !snapshot.IsCompletedAll;
            _transferSpeedLabel.Text = FormatTransferSpeed(snapshot.SpeedBytesPerSecond);
            _transferSpeedLabel.IsVisible = !snapshot.IsCompletedAll;
            _statusLabel.Text = snapshot.IsCompletedAll
                ? (snapshot.TotalBytes > 0
                    ? $"复制完成：{FormatBytes(snapshot.TotalBytes)}"
                    : $"复制完成：{snapshot.TotalFiles} 个文件")
                : (snapshot.TotalBytes > 0
                    ? $"正在复制：{FormatBytes(snapshot.DisplayedBytes)} / {FormatBytes(snapshot.TotalBytes)} · {snapshot.CompletedFiles} / {snapshot.TotalFiles}"
                    : $"正在复制：{snapshot.CompletedFiles} / {snapshot.TotalFiles} 个文件");
            if (snapshot.IsCompletedAll)
                EndTransferCancellation();
        }

        PostOrRunUi(Update);
    }

    private void HideVirtualExportProgress()
    {
        PostOrRunUi(() =>
        {
            _progressBar.IsVisible = false;
            _transferSpeedLabel.IsVisible = false;
            EndTransferCancellation();
        });
    }

    private void BeginFileTransfer()
    {
        if (Interlocked.Increment(ref _fileTransferDepth) == 1)
            StartMacSleepGuard();
        UpdateSourceRefreshEnabled();
    }

    private void EndFileTransfer()
    {
        if (Interlocked.Decrement(ref _fileTransferDepth) <= 0)
            StopMacSleepGuard();
        UpdateSourceRefreshEnabled();
    }

    private void StartMacSleepGuard()
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists("/usr/bin/caffeinate"))
            return;

        lock (_sleepGuardLock)
        {
            if (_caffeinateProcess is { HasExited: false })
                return;
            try
            {
                _caffeinateProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = "/usr/bin/caffeinate",
                    Arguments = $"-di -w {Environment.ProcessId}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch
            {
                _caffeinateProcess = null;
            }
        }
    }

    private void StopMacSleepGuard()
    {
        lock (_sleepGuardLock)
        {
            var proc = _caffeinateProcess;
            _caffeinateProcess = null;
            if (proc is null)
                return;
            try
            {
                if (!proc.HasExited)
                    proc.Kill();
            }
            catch { }
            finally
            {
                try { proc.Dispose(); } catch { }
            }
        }
    }

    private void UpdateSourceRefreshEnabled() =>
        PostOrRunUi(() => _refreshDisksButton.IsEnabled = Volatile.Read(ref _fileTransferDepth) == 0);

    private static void PostOrRunUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    private async Task RunBusyAsync(string status, Func<Task> action)
    {
        var isOutermostOperation = _busyDepth++ == 0;
        if (isOutermostOperation)
        {
            Cursor = new Cursor(StandardCursorType.Wait);
            _topContainer.IsEnabled = false;
            _tabs.IsEnabled = false;
        }
        AppendLog(status);
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            AppendLog("传输已取消。");
            _statusLabel.Text = "传输已取消";
        }
        catch (Exception ex)
        {
            AppendLog(ex.ToString());
            await ShowWarningAsync(ex.Message);
        }
        finally
        {
            if (--_busyDepth == 0)
            {
                _topContainer.IsEnabled = true;
                _tabs.IsEnabled = true;
                Cursor = Cursor.Default;
            }
        }
    }

    private CancellationToken BeginTransferCancellation()
    {
        EndTransferCancellation();
        _transferCts = new CancellationTokenSource();
        _cancelTransferButton.IsVisible = true;
        _cancelTransferButton.IsEnabled = true;
        return _transferCts.Token;
    }

    private void EndTransferCancellation()
    {
        var canceled = _transferCts?.IsCancellationRequested == true;
        _cancelTransferButton.IsVisible = false;
        _cancelTransferButton.IsEnabled = false;
        var cts = _transferCts;
        _transferCts = null;
        cts?.Dispose();
        if (canceled)
            _statusLabel.Text = "传输已取消";
    }

    private void RequestCancelTransfer()
    {
        try { _transferCts?.Cancel(); }
        catch { /* 忽略重复取消 */ }
        if (_cancelTransferButton.IsVisible)
        {
            _cancelTransferButton.IsEnabled = false;
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

    private sealed record DeviceItem(string Path, string Display)
    {
        public override string ToString() => Display;
    }

    private sealed record PfsClipboardSelection(string Name, bool IsDirectory, long Size);

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
        bool IsCompletedAll);

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
