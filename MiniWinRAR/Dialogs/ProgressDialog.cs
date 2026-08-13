using System.Drawing;
using System.Windows.Forms;
using MiniWinRAR.Core.Archive;

namespace MiniWinRAR;

/// <summary>
/// 非模态进度对话框：进度条 + 当前条目标签 + 取消按钮。
/// 由任务 10 用 Show(owner) 显示，把 Progress/Token 交给 IArchiveService；
/// 操作结束（成功/失败/取消）后由调用者 Close()。Complete() 只置 100% 并禁用取消，不自动关闭。
/// </summary>
public sealed class ProgressDialog : Form
{
    private readonly CancellationTokenSource _cts = new();
    private readonly ProgressBar _progressBar = new();
    private readonly Label _statusLabel = new();
    private readonly Button _cancelButton = new();
    private readonly IProgress<ProgressInfo> _progress;
    private bool _isCompleted;

    public ProgressDialog(string title)
    {
        Text = title;
        _progress = new DialogProgress(this);
        BuildUi();
        ConfigureForm();
    }

    /// <summary>传给 IArchiveService 的进度接收器（自动 marshal 到 UI 线程）。</summary>
    public IProgress<ProgressInfo> Progress => _progress;

    /// <summary>取消令牌：点击取消按钮会触发 CancellationTokenSource.Cancel()。</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>从任意线程上报进度；跨线程时用 BeginInvoke 回到 UI 线程更新控件。</summary>
    public void Report(ProgressInfo p)
    {
        if (IsDisposed) return;
        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => ApplyProgress(p)));
            }
            else
            {
                ApplyProgress(p);
            }
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
        {
            // 窗体已关闭/句柄销毁，忽略后续进度
        }
    }

    /// <summary>标记完成：进度置 100%、禁用取消按钮（不自动关闭，由调用者 Close()）。</summary>
    public void Complete()
    {
        if (IsDisposed) return;
        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(CompleteCore));
            }
            else
            {
                CompleteCore();
            }
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
        {
            // 同上
        }
    }

    private void BuildUi()
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 4), ColumnCount = 1, RowCount = 2 };
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _progressBar.Dock = DockStyle.Fill;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 100;
        _progressBar.Style = ProgressBarStyle.Blocks;
        _progressBar.Value = 0;
        _progressBar.Margin = new Padding(0, 0, 0, 6);

        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.AutoEllipsis = true;
        _statusLabel.Text = "准备中...";

        table.Controls.Add(_progressBar, 0, 0);
        table.Controls.Add(_statusLabel, 0, 1);

        _cancelButton.Text = "取消";
        _cancelButton.AutoSize = true;
        _cancelButton.Anchor = AnchorStyles.None;
        _cancelButton.Click += OnCancelClick;

        var buttonRow = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 46, ColumnCount = 2, RowCount = 1, Padding = new Padding(12, 8, 12, 8) };
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttonRow.Controls.Add(_cancelButton, 1, 0);

        Controls.Add(table);
        Controls.Add(buttonRow);
    }

    private void ConfigureForm()
    {
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        CancelButton = _cancelButton; // Esc 同样触发取消
        Size = new Size(440, 120);
        MinimumSize = new Size(400, 110);
        FormClosing += OnFormClosing;
    }

    private void ApplyProgress(ProgressInfo p)
    {
        if (IsDisposed) return;
        _progressBar.Value = Math.Clamp(p.Pct, _progressBar.Minimum, _progressBar.Maximum);
        if (!string.IsNullOrEmpty(p.Name)) _statusLabel.Text = p.Name;
    }

    private void CompleteCore()
    {
        if (IsDisposed) return;
        _progressBar.Value = _progressBar.Maximum;
        _statusLabel.Text = "完成";
        _cancelButton.Enabled = false;
        _isCompleted = true;
    }

    private void OnCancelClick(object? sender, EventArgs e)
    {
        _cts.Cancel();
        _cancelButton.Enabled = false;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // 操作尚未完成且未取消时关闭窗体 → 连带取消后台操作，避免操作无 UI 地继续
        if (!_isCompleted && !_cts.IsCancellationRequested) _cts.Cancel();
    }

    /// <summary>把 IProgress&lt;ProgressInfo&gt; 转发到对话框的 Report（内部做线程 marshal）。</summary>
    private sealed class DialogProgress : IProgress<ProgressInfo>
    {
        private readonly ProgressDialog _owner;
        public DialogProgress(ProgressDialog owner) => _owner = owner;
        public void Report(ProgressInfo value) => _owner.Report(value);
    }
}
