using System.IO;
using System.Drawing;
using System.Windows.Forms;

namespace MiniWinRAR;

/// <summary>
/// 主窗口：WinRAR 经典布局（菜单栏 / 工具栏 / 地址栏 / 文件列表 / 状态栏）。
/// 本任务实现文件系统浏览；归档操作（压缩 / 解压 / 打开归档 / 预览 / 拖拽）
/// 由任务 9 对话框与任务 10 事件接线完成。
/// </summary>
public sealed class MainForm : Form
{
    // 顶层控件（代码式布局，无 Designer/.resx）
    private readonly MenuStrip _menu = new();
    private readonly ToolStrip _toolStrip = new();
    private readonly ComboBox _addressBox = new();
    private readonly Button _goButton = new();
    private readonly ListView _fileList = new();
    private readonly ToolStripStatusLabel _statusPath = new();
    private readonly ToolStripStatusLabel _statusInfo = new();

    // 归档操作入口：本任务保持禁用，任务 10 启用并接线到对话框 + IArchiveService。
    private readonly ToolStripMenuItem _openArchiveItem = new();
    private readonly ToolStripMenuItem _compressItem = new();
    private readonly ToolStripMenuItem _extractItem = new();
    private readonly ToolStripButton _toolCompress = new("压缩");
    private readonly ToolStripButton _toolExtract = new("解压到");
    private readonly ToolStripButton _toolOpenArchive = new("打开归档");

    // 浏览状态
    private readonly List<string> _history = new();
    private string _currentDir;
    private long _dirTotalSize;
    private int _dirItemCount;

    public MainForm()
    {
        _currentDir = InitialDirectory();
        // Dock 布局按 Controls 逆序处理（后添加者先处理、取最外缘；Fill 按其处理时剩余矩形
        // 定位，故 Fill 必须最先添加最后处理）。添加顺序：ListView → StatusStrip → 地址栏 →
        // ToolStrip → MenuStrip，最终菜单最上、地址栏居中、列表填充中部、状态栏最下。
        BuildFileList();
        BuildStatusStrip();
        BuildAddressBar();
        BuildToolStrip();
        BuildMenu();
        ConfigureForm();
        NavigateTo(_currentDir);
    }

    private void BuildMenu()
    {
        _openArchiveItem.Text = "打开归档(&O)...";
        _openArchiveItem.ShortcutKeys = Keys.Control | Keys.O;
        _openArchiveItem.Enabled = false; // 任务 10 接线
        var exit = new ToolStripMenuItem("退出(&X)", null, (_, _) => Close());
        var file = new ToolStripMenuItem("文件(&F)");
        file.DropDownItems.Add(_openArchiveItem);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(exit);

        _compressItem.Text = "压缩(&A)...";
        _compressItem.ShortcutKeys = Keys.Control | Keys.A;
        _compressItem.Enabled = false; // 任务 10 接线
        _extractItem.Text = "解压到(&E)...";
        _extractItem.ShortcutKeys = Keys.Control | Keys.E;
        _extractItem.Enabled = false; // 任务 10 接线
        var commands = new ToolStripMenuItem("命令(&C)");
        commands.DropDownItems.Add(_compressItem);
        commands.DropDownItems.Add(_extractItem);

        var tools = new ToolStripMenuItem("工具(&T)");
        tools.DropDownItems.Add(new ToolStripMenuItem("刷新(&R)", null, (_, _) => RefreshView(), Keys.F5));
        tools.DropDownItems.Add(new ToolStripMenuItem("返回上级(&U)", null, (_, _) => GoUp()));

        var options = new ToolStripMenuItem("选项(&O)");
        options.DropDownItems.Add(new ToolStripMenuItem("设置...") { Enabled = false }); // 后续版本占位

        var help = new ToolStripMenuItem("帮助(&H)");
        help.DropDownItems.Add(new ToolStripMenuItem("关于(&A)", null, (_, _) => ShowAbout()));

        _menu.Items.Add(file);
        _menu.Items.Add(commands);
        _menu.Items.Add(tools);
        _menu.Items.Add(options);
        _menu.Items.Add(help);
        _menu.Dock = DockStyle.Top;
        Controls.Add(_menu);
    }

    private void BuildToolStrip()
    {
        _toolCompress.Enabled = false;   // 任务 10 接线
        _toolExtract.Enabled = false;    // 任务 10 接线
        _toolOpenArchive.Enabled = false; // 任务 10 接线
        var refresh = new ToolStripButton("刷新", null, (_, _) => RefreshView());
        var goUp = new ToolStripButton("返回上级", null, (_, _) => GoUp());

        _toolStrip.Items.Add(_toolCompress);
        _toolStrip.Items.Add(_toolExtract);
        _toolStrip.Items.Add(_toolOpenArchive);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(refresh);
        _toolStrip.Items.Add(goUp);
        _toolStrip.Dock = DockStyle.Top;
        Controls.Add(_toolStrip);
    }

    private void BuildAddressBar()
    {
        var panel = new Panel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(8, 4, 8, 4) };
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var label = new Label
        {
            Text = "地址(&D):",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 6, 0),
        };

        _addressBox.Dock = DockStyle.Fill;
        _addressBox.DropDownStyle = ComboBoxStyle.DropDown; // 可编辑：显示/输入路径
        _addressBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _addressBox.AutoCompleteSource = AutoCompleteSource.FileSystemDirectories;
        _addressBox.Margin = new Padding(0, 4, 0, 4);
        _addressBox.KeyDown += OnAddressKeyDown;
        _addressBox.SelectionChangeCommitted += (_, _) => GoToAddress();

        _goButton.Text = "转到(&G)";
        _goButton.AutoSize = true;
        _goButton.Margin = new Padding(6, 0, 0, 0);
        _goButton.Click += (_, _) => GoToAddress();

        table.Controls.Add(label, 0, 0);
        table.Controls.Add(_addressBox, 1, 0);
        table.Controls.Add(_goButton, 2, 0);
        panel.Controls.Add(table);
        Controls.Add(panel);
    }

    private void BuildFileList()
    {
        _fileList.Dock = DockStyle.Fill;
        _fileList.View = View.Details;
        _fileList.FullRowSelect = true;
        _fileList.MultiSelect = true;
        _fileList.HideSelection = false;
        _fileList.AllowDrop = true; // 拖拽事件（DragEnter/DragDrop）由任务 10 接线
        _fileList.Columns.Add("名称", 360);
        _fileList.Columns.Add("大小", 100, HorizontalAlignment.Right);
        _fileList.Columns.Add("类型", 150);
        _fileList.Columns.Add("修改时间", 170);
        _fileList.ItemActivate += OnFileListActivate;
        _fileList.SelectedIndexChanged += (_, _) => UpdateStatus();
        _fileList.KeyDown += OnFileListKeyDown;
        Controls.Add(_fileList);
    }

    private void BuildStatusStrip()
    {
        _statusPath.Spring = true;
        _statusPath.TextAlign = ContentAlignment.MiddleLeft;
        _statusInfo.TextAlign = ContentAlignment.MiddleRight;
        var strip = new StatusStrip();
        strip.Items.Add(_statusPath);
        strip.Items.Add(_statusInfo);
        strip.Dock = DockStyle.Bottom;
        Controls.Add(strip);
    }

    private void ConfigureForm()
    {
        Text = "Mini-WinRAR";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(960, 620);
        MinimumSize = new Size(600, 380);
        AllowDrop = true; // 拖拽事件由任务 10 接线
        _fileList.Resize += (_, _) => FitNameColumn();
    }

    // ---- 文件系统浏览 ----

    private static string InitialDirectory()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile) && Directory.Exists(profile)) return profile;
        return Environment.CurrentDirectory;
    }

    /// <summary>解析并进入目录；无效路径返回 null。</summary>
    private string? ResolveDirectory(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var text = input.Trim().Trim('"'); // 去掉 Explorer 复制路径带的外层引号
        if (text.Length == 0) return null;

        string full;
        try
        {
            full = Path.GetFullPath(text, _currentDir);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        // 裸盘符 "C:" 补成根路径 "C:\"
        if (full.Length == 2 && full[1] == ':') full += Path.DirectorySeparatorChar;
        return Directory.Exists(full) ? full : null;
    }

    private void NavigateTo(string path)
    {
        var resolved = ResolveDirectory(path);
        if (resolved == null)
        {
            MessageBox.Show(this, $"无法打开该路径:\n{path}", "Mini-WinRAR",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _currentDir = resolved;
        RefreshView();
        PushHistory(resolved);
        UpdateAddressBox();
    }

    private void RefreshView()
    {
        _dirTotalSize = 0;
        _dirItemCount = 0;
        string? listingError = null;

        _fileList.BeginUpdate();
        try
        {
            _fileList.Items.Clear();

            var parent = Directory.GetParent(_currentDir);
            if (parent != null)
            {
                var up = new ListViewItem("..");
                up.SubItems.Add("");          // 大小
                up.SubItems.Add("文件夹");    // 类型
                up.SubItems.Add("");          // 修改时间
                up.Tag = new EntryTag("..", parent.FullName, IsDir: true, IsUp: true, Size: 0);
                _fileList.Items.Add(up);
            }

            List<string> dirs;
            try
            {
                dirs = Directory.EnumerateDirectories(_currentDir).ToList();
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
            {
                dirs = new List<string>();
                listingError ??= e.Message;
            }

            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(_currentDir).ToList();
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
            {
                files = new List<string>();
                listingError ??= e.Message;
            }

            foreach (var d in dirs.OrderBy(d => Path.GetFileName(d), StringComparer.CurrentCultureIgnoreCase))
            {
                AddDirectory(d);
            }

            foreach (var f in files.OrderBy(f => Path.GetFileName(f), StringComparer.CurrentCultureIgnoreCase))
            {
                TryAddFile(f); // 文件在枚举后消失/被占用时静默跳过
            }
        }
        finally
        {
            _fileList.EndUpdate();
        }

        FitNameColumn();
        UpdateStatus(listingError);
    }

    private void AddDirectory(string fullPath)
    {
        var name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string mtime;
        try { mtime = Directory.GetLastWriteTime(fullPath).ToString("yyyy-MM-dd HH:mm"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { mtime = ""; }

        var item = new ListViewItem("📁  " + name);
        item.SubItems.Add("");                     // 大小
        item.SubItems.Add("文件夹");               // 类型
        item.SubItems.Add(mtime);                  // 修改时间
        item.Tag = new EntryTag(name, fullPath, IsDir: true, IsUp: false, Size: 0);
        _fileList.Items.Add(item);
        _dirItemCount++;
    }

    private bool TryAddFile(string fullPath)
    {
        try
        {
            var fi = new FileInfo(fullPath);
            var item = new ListViewItem(fi.Name);
            item.SubItems.Add(FormatSize(fi.Length));                       // 大小
            item.SubItems.Add(GetTypeText(fi.Extension));                   // 类型
            item.SubItems.Add(fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm")); // 修改时间
            item.Tag = new EntryTag(fi.Name, fi.FullName, IsDir: false, IsUp: false, Size: fi.Length);
            _fileList.Items.Add(item);
            _dirItemCount++;
            _dirTotalSize += fi.Length;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private void GoUp()
    {
        var parent = Directory.GetParent(_currentDir);
        if (parent != null) NavigateTo(parent.FullName);
    }

    private void GoToAddress()
    {
        var text = _addressBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        NavigateTo(text);
        UpdateAddressBox();
    }

    private void PushHistory(string full)
    {
        _history.Remove(full);
        _history.Insert(0, full);
        _addressBox.Items.Clear();
        foreach (var h in _history.Take(20)) _addressBox.Items.Add(h);
    }

    private void UpdateAddressBox() => _addressBox.Text = _currentDir;

    private void UpdateStatus(string? listingError = null)
    {
        if (_fileList.SelectedItems.Count > 0)
        {
            long total = 0;
            foreach (ListViewItem i in _fileList.SelectedItems)
            {
                total += (i.Tag as EntryTag)?.Size ?? 0;
            }
            _statusInfo.Text = $"已选 {_fileList.SelectedItems.Count} 项 · 共 {FormatSize(total)}";
        }
        else
        {
            _statusInfo.Text = $"{_dirItemCount} 个对象 · {FormatSize(_dirTotalSize)}";
        }

        _statusPath.Text = string.IsNullOrEmpty(listingError)
            ? _currentDir
            : _currentDir + "（部分内容无法读取）";
    }

    /// <summary>名称列自动拉伸占满剩余宽度。</summary>
    private void FitNameColumn()
    {
        if (_fileList.Columns.Count < 4) return;
        var others = 0;
        for (var i = 1; i < _fileList.Columns.Count; i++) others += _fileList.Columns[i].Width;
        var name = Math.Max(80, _fileList.ClientSize.Width - others - SystemInformation.VerticalScrollBarWidth - 4);
        _fileList.Columns[0].Width = name;
    }

    // ---- 事件 ----

    private void OnFileListActivate(object? sender, EventArgs e)
    {
        if (_fileList.SelectedItems.Count == 0) return;
        var tag = _fileList.SelectedItems[0].Tag as EntryTag;
        if (tag == null) return;
        if (tag.IsUp) { GoUp(); return; }
        if (tag.IsDir) { NavigateTo(tag.FullPath); return; }
        // 文件双击（打开/预览）由任务 10 接线。
    }

    private void OnFileListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Back)
        {
            GoUp();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.F5)
        {
            RefreshView();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            GoToAddress();
        }
    }

    private void ShowAbout()
    {
        MessageBox.Show(this,
            $"Mini-WinRAR {Application.ProductVersion}\n\n" +
            "迷你压缩/解压工具，支持 ZIP 与 .mwr 格式。",
            "关于 Mini-WinRAR", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ---- 辅助 ----

    /// <summary>ListView 条目附带的数据（含真实路径与是否为目录/上级）。</summary>
    private sealed record EntryTag(string Name, string FullPath, bool IsDir, bool IsUp, long Size);

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double value = bytes;
        var i = -1;
        string[] units = { "KB", "MB", "GB", "TB" };
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return $"{value:0.#} {units[i]}";
    }

    private static string GetTypeText(string ext)
    {
        switch (ext.ToLowerInvariant())
        {
            case ".txt": case ".log": case ".md": case ".json": case ".xml":
            case ".ini": case ".config": case ".py": case ".cs": case ".js":
                return "文本文件";
            case ".zip": case ".rar": case ".7z": case ".tar": case ".gz": case ".bz2":
            case ".xz": case ".iso":
                return "压缩文件";
            case ".mwr":
                return "Mini-WinRAR 归档";
            case ".exe": case ".bat": case ".cmd": case ".com": case ".msi":
                return "应用程序";
            case ".png": case ".jpg": case ".jpeg": case ".gif": case ".bmp":
            case ".webp": case ".tif": case ".tiff":
                return "图片";
            case ".mp3": case ".wav": case ".flac": case ".ogg": case ".aac":
            case ".m4a":
                return "音频";
            case ".mp4": case ".mkv": case ".avi": case ".mov": case ".wmv":
            case ".flv":
                return "视频";
            case ".pdf":
                return "PDF 文档";
            case ".doc": case ".docx":
                return "Word 文档";
            case ".xls": case ".xlsx": case ".csv":
                return "Excel 表格";
            case ".ppt": case ".pptx":
                return "PowerPoint 演示文稿";
            case ".htm": case ".html":
                return "网页";
            case ".dll":
                return "动态链接库";
            default:
                return "文件";
        }
    }
}
