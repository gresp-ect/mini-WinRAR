using System.Drawing;
using System.IO;
using System.Windows.Forms;
using MiniWinRAR.Core.Archive;

namespace MiniWinRAR;

/// <summary>
/// 压缩设置对话框：选择归档格式（zip/mwr）、压缩级别与可选密码。
/// 代码式布局，无 Designer/.resx。模态调用：ShowDialog 返回 OK 后读取 Format/Level/Password。
/// 源路径仅用于标题与摘要显示；空选择由调用方（任务 10）处理，本对话框不拦截。
/// </summary>
public sealed class CompressDialog : Form
{
    private readonly ComboBox _formatBox = new();
    private readonly ComboBox _levelBox = new();
    private readonly CheckBox _usePasswordCheck = new();
    private readonly TextBox _passwordBox = new();
    private readonly Button _okButton = new();
    private readonly Button _cancelButton = new();
    private readonly IReadOnlyList<string> _sourcePaths;

    public CompressDialog(IReadOnlyList<string> sourcePaths)
    {
        _sourcePaths = sourcePaths;
        BuildUi();
        ConfigureForm();
    }

    /// <summary>所选格式："zip" 或 "mwr"。</summary>
    public string Format => _formatBox.SelectedItem is FormatItem f ? f.Value : "zip";

    /// <summary>所选压缩级别（默认 Fast）。</summary>
    public CompressionLevel Level => _levelBox.SelectedItem is LevelItem l ? l.Value : CompressionLevel.Fast;

    /// <summary>勾选"使用密码"且输入非空时为密码，否则为 null（不加密）。</summary>
    public string? Password => _usePasswordCheck.Checked && !string.IsNullOrWhiteSpace(_passwordBox.Text)
        ? _passwordBox.Text
        : null;

    private void BuildUi()
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 4), ColumnCount = 2, RowCount = 5 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));   // 摘要
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 格式
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 级别
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 密码
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 撑开

        var summary = new Label
        {
            Text = DescribeSources(),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(0, 0, 0, 6),
        };
        table.Controls.Add(summary, 0, 0);
        table.SetColumnSpan(summary, 2);

        var formatLabel = new Label
        {
            Text = "格式(&F):",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 8, 8),
        };
        _formatBox.Dock = DockStyle.Fill;
        _formatBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _formatBox.Margin = new Padding(0, 0, 0, 8);
        _formatBox.Items.Add(new FormatItem("zip", "ZIP 压缩文件 (*.zip)"));
        _formatBox.Items.Add(new FormatItem("mwr", "Mini-WinRAR 归档 (*.mwr)"));
        _formatBox.SelectedIndex = 0; // 默认 zip
        table.Controls.Add(formatLabel, 0, 1);
        table.Controls.Add(_formatBox, 1, 1);

        var levelLabel = new Label
        {
            Text = "压缩级别(&C):",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 8, 8),
        };
        _levelBox.Dock = DockStyle.Fill;
        _levelBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _levelBox.Margin = new Padding(0, 0, 0, 8);
        _levelBox.Items.Add(new LevelItem(CompressionLevel.Store, "Store（不压缩）"));
        _levelBox.Items.Add(new LevelItem(CompressionLevel.Fast, "Fast（快速）"));
        _levelBox.Items.Add(new LevelItem(CompressionLevel.Best, "Best（最佳）"));
        _levelBox.SelectedIndex = 1; // 默认 Fast
        table.Controls.Add(levelLabel, 0, 2);
        table.Controls.Add(_levelBox, 1, 2);

        _usePasswordCheck.Text = "使用密码加密(&P)";
        _usePasswordCheck.AutoSize = true;
        _usePasswordCheck.Anchor = AnchorStyles.Left;
        _usePasswordCheck.TextAlign = ContentAlignment.MiddleLeft;
        _usePasswordCheck.Margin = new Padding(0, 0, 8, 0);
        _usePasswordCheck.CheckedChanged += (_, _) => _passwordBox.Enabled = _usePasswordCheck.Checked;

        _passwordBox.Dock = DockStyle.Fill;
        _passwordBox.UseSystemPasswordChar = true;
        _passwordBox.Enabled = false;
        table.Controls.Add(_usePasswordCheck, 0, 3);
        table.Controls.Add(_passwordBox, 1, 3);

        _okButton.Text = "确定(&O)";
        _okButton.AutoSize = true;
        _okButton.Click += OnOk;
        _cancelButton.Text = "取消(&C)";
        _cancelButton.AutoSize = true;
        _cancelButton.DialogResult = DialogResult.Cancel;

        Controls.Add(table);
        Controls.Add(CreateButtonRow(_okButton, _cancelButton));
    }

    private void ConfigureForm()
    {
        Text = DescribeTitle();
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AcceptButton = _okButton;
        CancelButton = _cancelButton;
        Size = new Size(460, 230);
        MinimumSize = new Size(420, 210);
    }

    private void OnOk(object? sender, EventArgs e)
    {
        if (_formatBox.SelectedItem == null || _levelBox.SelectedItem == null)
        {
            MessageBox.Show(this, "请选择归档格式与压缩级别。", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // 勾选"使用密码"却留空：直接提交会静默生成未加密归档，属于误操作，拦截并提示。
        if (_usePasswordCheck.Checked && string.IsNullOrWhiteSpace(_passwordBox.Text))
        {
            MessageBox.Show(this, "已勾选“使用密码加密”，请输入密码；不需要加密请取消勾选。", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        DialogResult = DialogResult.OK;
    }

    private string DescribeTitle() => _sourcePaths.Count switch
    {
        0 => "压缩",
        1 => "压缩 — " + SingleSourceName(),
        _ => $"压缩 — {_sourcePaths.Count} 项",
    };

    private string DescribeSources() => _sourcePaths.Count switch
    {
        0 => "未选择任何项目。",
        1 => "将压缩：" + SingleSourceName(),
        _ => $"将压缩 {_sourcePaths.Count} 个所选对象。",
    };

    private string SingleSourceName()
    {
        var trimmed = _sourcePaths[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
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

    /// <summary>ComboBox 项：Value 为服务用格式字符串，Label 为显示文本。</summary>
    private sealed record FormatItem(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>ComboBox 项：Value 为压缩级别枚举，Label 为显示文本。</summary>
    private sealed record LevelItem(CompressionLevel Value, string Label)
    {
        public override string ToString() => Label;
    }
}
