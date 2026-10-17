using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace PfsExplorerLite;

internal sealed record PartitionCreateRequest(string Name, string Size);

internal static class MessageDialog
{
    public static Task ShowWarningAsync(Window owner, string title, string message) =>
        ShowSingleButtonAsync(owner, title, message);

    public static Task ShowInfoAsync(Window owner, string title, string message) =>
        ShowSingleButtonAsync(owner, title, message);

    private static async Task ShowSingleButtonAsync(Window owner, string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var ok = new Button
        {
            Content = "确定",
            Width = 100,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = true,
            IsCancel = true
        };
        ok.Click += (_, _) => dialog.Close();

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 16, 0, 0)
        };
        buttonRow.Children.Add(ok);

        var body = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 480
        });
        body.Children.Add(buttonRow);

        dialog.Content = body;
        await dialog.ShowDialog(owner);
    }

    public static async Task<bool> ConfirmYesNoAsync(
        Window owner,
        string title,
        string message,
        bool defaultNo = false)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 560,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var yes = new Button
        {
            Content = "是(Y)",
            Width = 100,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = !defaultNo
        };
        var no = new Button
        {
            Content = "否(N)",
            Width = 100,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = defaultNo,
            IsCancel = true
        };

        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 16, 0, 0)
        };
        buttonRow.Children.Add(yes);
        buttonRow.Children.Add(no);

        var body = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520
        });
        body.Children.Add(buttonRow);

        dialog.Content = body;
        var result = await dialog.ShowDialog<bool?>(owner);
        return result == true;
    }

    public static async Task<bool> ConfirmOkCancelAsync(
        Window owner,
        string title,
        string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 560,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var ok = new Button
        {
            Content = "确定",
            Width = 100,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = true
        };
        var cancel = new Button
        {
            Content = "取消",
            Width = 100,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsCancel = true
        };

        ok.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 16, 0, 0)
        };
        buttonRow.Children.Add(ok);
        buttonRow.Children.Add(cancel);

        var body = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520
        });
        body.Children.Add(buttonRow);

        dialog.Content = body;
        var result = await dialog.ShowDialog<bool?>(owner);
        return result == true;
    }
}

internal static class Prompt
{
    public static async Task<string?> ShowAsync(Window owner, string title, string label, string initialValue = "")
    {
        var dialog = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var textBox = new TextBox { Text = initialValue };
        var ok = new Button
        {
            Content = "确定",
            Width = 100,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = true
        };
        var cancel = new Button
        {
            Content = "取消",
            Width = 100,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsCancel = true
        };

        ok.Click += (_, _) => dialog.Close(textBox.Text?.Trim());
        cancel.Click += (_, _) => dialog.Close(null);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 12, 0, 0)
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var body = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        body.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(textBox);
        body.Children.Add(buttons);

        dialog.Content = body;
        dialog.Opened += (_, _) =>
        {
            textBox.Focus();
            textBox.SelectAll();
        };
        return await dialog.ShowDialog<string?>(owner);
    }
}

internal static class OverwriteConfirmation
{
    public static async Task<bool> ShowAsync(Window owner)
    {
        var dialog = new Window
        {
            Title = "发现同名项目",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var overwrite = new Button
        {
            Content = "全部覆盖",
            Width = 110,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = true
        };
        var cancel = new Button
        {
            Content = "取消",
            Width = 100,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsCancel = true
        };

        overwrite.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 14, 0, 0)
        };
        buttons.Children.Add(overwrite);
        buttons.Children.Add(cancel);

        var body = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        body.Children.Add(new TextBlock
        {
            Text = "目标 PFS 目录中发现同名文件或目录。\n选择“全部覆盖”将替换同名文件，并合并同名目录。",
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(buttons);

        dialog.Content = body;
        var result = await dialog.ShowDialog<bool?>(owner);
        return result == true;
    }
}

internal static class PartitionCreatePrompt
{
    public static async Task<PartitionCreateRequest?> ShowAsync(Window owner, string freeSpace, string defaultSize)
    {
        var dialog = new Window
        {
            Title = "新建 PFS 分区",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var description = new TextBlock
        {
            Text = $"当前 APA 未分配空间：{freeSpace}\n分区名可使用 DATA 或 +DATA；容量必须是 128MiB 的整数倍。\n大于单个 APA 分区上限时，会由 PS2SDK 自动建立主分区和子分区。",
            TextWrapping = TextWrapping.Wrap
        };

        var nameBox = new TextBox { Text = "+DATA", Margin = new Thickness(0, 0, 0, 10) };
        var sizeBox = new TextBox { Text = defaultSize, Margin = new Thickness(0, 0, 0, 10) };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("90,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto")
        };

        var nameLabel = new TextBlock { Text = "分区名称：", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) };
        Grid.SetRow(nameLabel, 0);
        Grid.SetColumn(nameLabel, 0);
        Grid.SetRow(nameBox, 0);
        Grid.SetColumn(nameBox, 1);

        var sizeLabel = new TextBlock { Text = "容量：", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) };
        Grid.SetRow(sizeLabel, 1);
        Grid.SetColumn(sizeLabel, 0);
        Grid.SetRow(sizeBox, 1);
        Grid.SetColumn(sizeBox, 1);

        var hintLabel = new TextBlock
        {
            Text = "例如：512G 或 128M",
            Foreground = Brushes.Gray
        };
        Grid.SetRow(hintLabel, 2);
        Grid.SetColumn(hintLabel, 1);

        grid.Children.Add(nameLabel);
        grid.Children.Add(nameBox);
        grid.Children.Add(sizeLabel);
        grid.Children.Add(sizeBox);
        grid.Children.Add(hintLabel);

        var ok = new Button
        {
            Content = "下一步",
            Width = 110,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = true
        };
        var cancel = new Button
        {
            Content = "取消",
            Width = 100,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsCancel = true
        };

        ok.Click += (_, _) => dialog.Close(new PartitionCreateRequest(nameBox.Text?.Trim() ?? string.Empty, sizeBox.Text?.Trim() ?? string.Empty));
        cancel.Click += (_, _) => dialog.Close(null);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 12, 0, 0)
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var body = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
        body.Children.Add(description);
        body.Children.Add(grid);
        body.Children.Add(buttons);

        dialog.Content = body;
        return await dialog.ShowDialog<PartitionCreateRequest?>(owner);
    }
}

internal static class PartitionCreateConfirmation
{
    public static async Task<bool> ShowAsync(Window owner, string source, string name, string size)
    {
        var dialog = new Window
        {
            Title = "确认写入 APA 分区表",
            Width = 580,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var message = new TextBlock
        {
            Text = $"将在以下来源的“当前未分配 APA 空间”中创建新的 PFS 逻辑卷：\n{source}\n\n分区：{name}\n容量：{size}\n\n此操作会写入分区表。请勿断电、拔出硬盘或强制关闭程序。",
            TextWrapping = TextWrapping.Wrap
        };

        var acknowledge = new CheckBox
        {
            Content = "我确认目标来源正确，并已备份重要数据",
            IsChecked = false
        };

        var create = new Button
        {
            Content = "创建",
            Width = 110,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsEnabled = false,
            IsDefault = true
        };
        var cancel = new Button
        {
            Content = "取消",
            Width = 100,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsCancel = true
        };

        acknowledge.IsCheckedChanged += (_, _) => create.IsEnabled = acknowledge.IsChecked == true;
        create.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 12, 0, 0)
        };
        buttons.Children.Add(create);
        buttons.Children.Add(cancel);

        var body = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
        body.Children.Add(message);
        body.Children.Add(acknowledge);
        body.Children.Add(buttons);

        dialog.Content = body;
        var result = await dialog.ShowDialog<bool?>(owner);
        return result == true;
    }
}
