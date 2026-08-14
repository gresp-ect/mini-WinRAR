using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace MiniWinRAR;

/// <summary>
/// 解压设置对话框：选择目标目录与（可选）密码。
/// 仅当归档需要密码（构造参数 needsPassword）时渲染密码输入行；否则整行不显示、Password 返回 null。
/// 代码式布局，无 Designer/.resx。模态调用：ShowDialog 返回 OK 后读取 TargetDirectory/Password。
/// </summary>
public sealed class ExtractDialog : Form
{
    private readonly TextBox _targetBox = new();
    private readonly Button _browseButton = new();
    private readonly TextBox _passwordBox = new();
    private readonly Button _okButton = new();
    private readonly Button _cancelButton = new();
    private readonly bool _needsPassword;

    public ExtractDialog(bool needsPassword)
    {
        _needsPassword = needsPassword;
        BuildUi();
        ConfigureForm();
    }

    /// <summary>解压目标目录（去首尾空白）。</summary>
    public string TargetDirectory => _targetBox.Text.Trim();

    /// <summary>归档需要密码且填写密码时为密码，否则为 null（无密码）。</summary>
    public string? Password => _needsPassword && !string.IsNullOrWhiteSpace(_passwordBox.Text)
        ? _passwordBox.Text
        : null;

    private void BuildUi()
    {
        // 行：目标目录 + [密码（仅需要时）] + 撑开
        var rowCount = _needsPassword ? 3 : 2;
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 4), ColumnCount = 3, RowCount = rowCount };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 目标目录
        if (_needsPassword) table.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 密码
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 撑开

        var targetLabel = new Label
        {
            Text = "目标目录(&T):",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 8, 8),
        };
        _targetBox.Dock = DockStyle.Fill;
        _targetBox.Margin = new Padding(0, 0, 8, 8);
        _targetBox.TextChanged += (_, _) => _okButton.Enabled = !string.IsNullOrWhiteSpace(_targetBox.Text);

        _browseButton.Text = "浏览(&B)...";
        _browseButton.Dock = DockStyle.Fill; // 与目标目录输入框等高（同填满单元格行高），避免遮挡
        _browseButton.Margin = new Padding(0, 0, 0, 8);
        _browseButton.Click += OnBrowse;

        table.Controls.Add(targetLabel, 0, 0);
        table.Controls.Add(_targetBox, 1, 0);
        table.Controls.Add(_browseButton, 2, 0);

        // 归档需要密码时才渲染密码行（整行不显示，而非靠窗口大小隐藏）
        if (_needsPassword)
        {
            var passwordLabel = new Label
            {
                Text = "密码(&P):",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 0, 8, 0),
            };
            _passwordBox.Dock = DockStyle.Fill;
            _passwordBox.UseSystemPasswordChar = true;

            table.Controls.Add(passwordLabel, 0, 1);
            table.Controls.Add(_passwordBox, 1, 1);
            table.SetColumnSpan(_passwordBox, 2);
        }

        _okButton.Text = "确定(&O)";
        _okButton.AutoSize = true;
        _okButton.Enabled = false; // 目标目录为空时禁用
        _okButton.Click += OnOk;
        _cancelButton.Text = "取消(&C)";
        _cancelButton.AutoSize = true;
        _cancelButton.DialogResult = DialogResult.Cancel;

        Controls.Add(table);
        Controls.Add(CreateButtonRow(_okButton, _cancelButton));
    }

    private void ConfigureForm()
    {
        Text = "解压到";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable; // 可拖拽改变大小（原 FixedDialog 固定尺寸）
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AcceptButton = _okButton;
        CancelButton = _cancelButton;
        Size = new Size(520, 160);
        MinimumSize = new Size(440, 150);
    }

    private void OnBrowse(object? sender, EventArgs e)
    {
        using var fbd = new FolderBrowserDialog
        {
            Description = "选择解压目标目录",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        var current = _targetBox.Text.Trim();
        if (Directory.Exists(current)) fbd.SelectedPath = current;
        if (fbd.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(fbd.SelectedPath))
        {
            _targetBox.Text = fbd.SelectedPath;
        }
    }

    private void OnOk(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_targetBox.Text)) return; // 按钮已禁用，此处仅防御
        DialogResult = DialogResult.OK;
    }

    private static TableLayoutPanel CreateButtonRow(Button ok, Button cancel)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 46, ColumnCount = 3, RowCount = 1, Padding = new Padding(12, 8, 12, 8) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ok.Anchor = AnchorStyles.None;
        cancel.Anchor = AnchorStyles.None;
        row.Controls.Add(cancel, 1, 0);
        row.Controls.Add(ok, 2, 0);
        return row;
    }
}
