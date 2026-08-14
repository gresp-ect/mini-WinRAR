using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using MiniWinRAR.Core.Archive;
using MiniWinRAR.Core.Crypto;

namespace MiniWinRAR;

/// <summary>
/// 主窗口：WinRAR 经典布局（菜单栏 / 工具栏 / 地址栏 / 文件列表 / 状态栏）。
/// 支持文件系统浏览与归档视图（打开 .zip/.mwr 后列出条目）；压缩 / 解压 / 预览
/// 一律在后台线程执行，经 ProgressDialog 上报进度并支持取消。
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

    // 归档操作入口（工具栏 + 菜单共用同一批处理器）
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

    // 归档视图状态：_archivePath 非 null 时列表显示归档条目而非文件系统目录
    private string? _archivePath;
    private IArchiveService? _archiveService;
    private List<ArchiveEntry> _archiveEntries = new();
    private bool _archiveEncrypted;
    private string? _archivePassword;

    // 归档操作进行中（非模态进度对话框）：防止重叠操作，进入时禁用各入口，finally 恢复。
    private bool _operationBusy;

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
        _openArchiveItem.Enabled = true;
        _openArchiveItem.Click += (_, _) => OnOpenArchive();
        var exit = new ToolStripMenuItem("退出(&X)", null, (_, _) => Close());
        var file = new ToolStripMenuItem("文件(&F)");
        file.DropDownItems.Add(_openArchiveItem);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(exit);

        _compressItem.Text = "压缩(&A)...";
        _compressItem.ShortcutKeys = Keys.Control | Keys.A;
        _compressItem.Enabled = true;
        _compressItem.Click += (_, _) => OnCompress();
        _extractItem.Text = "解压到(&E)...";
        _extractItem.ShortcutKeys = Keys.Control | Keys.E;
        _extractItem.Enabled = false; // 仅归档视图内可用（UpdateOperationButtons 动态切换）
        _extractItem.Click += (_, _) => OnExtract();
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
        _toolCompress.Enabled = true;
        _toolCompress.Click += (_, _) => OnCompress();
        _toolExtract.Enabled = false; // 仅归档视图内可用
        _toolExtract.Click += (_, _) => OnExtract();
        _toolOpenArchive.Enabled = true;
        _toolOpenArchive.Click += (_, _) => OnOpenArchive();
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
        _fileList.AllowDrop = true;
        _fileList.Columns.Add("名称", 360);
        _fileList.Columns.Add("大小", 100, HorizontalAlignment.Right);
        _fileList.Columns.Add("类型", 150);
        _fileList.Columns.Add("修改时间", 170);
        _fileList.ItemActivate += OnFileListActivate;
        _fileList.SelectedIndexChanged += (_, _) => { UpdateStatus(); UpdateOperationButtons(); };
        _fileList.KeyDown += OnFileListKeyDown;
        _fileList.DragEnter += OnDragEnter;
        _fileList.DragDrop += OnDragDrop;
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
        AllowDrop = true;
        DragEnter += OnDragEnter; // 空白区也可接受拖拽
        DragDrop += OnDragDrop;
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

        // 裸盘符 "C:" → "C:\"。必须在 GetFullPath 之前规范化：GetFullPath("C:", base)
        // 会解析为 C 盘的当前目录而非根目录，事后补全分支是死代码。
        if (text.Length == 2 && text[1] == ':') text += Path.DirectorySeparatorChar;

        string full;
        try
        {
            full = Path.GetFullPath(text, _currentDir);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return Directory.Exists(full) ? full : null;
    }

    private void NavigateTo(string path)
    {
        ExitArchiveMode(); // 进入文件系统目录即退出归档视图
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
        if (_archivePath != null)
        {
            RefreshArchiveView();
            return;
        }

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
        if (_archivePath != null)
        {
            ExitArchiveMode();
            RefreshView();
            UpdateAddressBox();
            return;
        }
        var parent = Directory.GetParent(_currentDir);
        if (parent != null) NavigateTo(parent.FullName);
    }

    private void GoToAddress()
    {
        var text = _addressBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        // 归档视图内地址栏显示归档路径：文本未变时不重复跳转（否则误报"无法打开该路径"）
        if (_archivePath != null && string.Equals(text.Trim().Trim('"'), _archivePath, StringComparison.OrdinalIgnoreCase))
            return;
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

    private void UpdateAddressBox() => _addressBox.Text = _archivePath ?? _currentDir;

    private void UpdateStatus(string? listingError = null)
    {
        if (_fileList.SelectedItems.Count > 0)
        {
            long total = 0;
            foreach (ListViewItem i in _fileList.SelectedItems)
            {
                total += i.Tag switch
                {
                    EntryTag e => e.Size,
                    ArchiveEntryTag a => a.Size,
                    _ => 0,
                };
            }
            _statusInfo.Text = $"已选 {_fileList.SelectedItems.Count} 项 · 共 {FormatSize(total)}";
        }
        else
        {
            _statusInfo.Text = $"{_dirItemCount} 个对象 · {FormatSize(_dirTotalSize)}";
        }

        var current = _archivePath ?? _currentDir;
        _statusPath.Text = string.IsNullOrEmpty(listingError)
            ? current
            : current + "（部分内容无法读取）";
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

    // ---- 归档视图 ----

    /// <summary>列出归档条目（只读的内存元数据，进入归档视图）。</summary>
    private void RefreshArchiveView()
    {
        _dirTotalSize = 0;
        _dirItemCount = 0;

        _fileList.BeginUpdate();
        try
        {
            _fileList.Items.Clear();

            // 返回上级项：退回归档所在目录的文件系统视图
            var up = new ListViewItem("..");
            up.SubItems.Add("");          // 大小
            up.SubItems.Add("文件夹");    // 类型
            up.SubItems.Add("");          // 修改时间
            up.Tag = new ArchiveEntryTag("", 0, IsDir: true, IsUp: true);
            _fileList.Items.Add(up);

            foreach (var entry in _archiveEntries
                         .OrderBy(e => e.IsDir ? 0 : 1)
                         .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = new ListViewItem((entry.IsDir ? "📁  " : "") + entry.Name);
                item.SubItems.Add(entry.IsDir ? "" : FormatSize(entry.Size));   // 大小
                var typeText = entry.IsDir ? "文件夹" : GetTypeText(Path.GetExtension(entry.Name));
                if (entry.IsEncrypted) typeText += "（加密）";                   // 类型
                item.SubItems.Add(typeText);
                item.SubItems.Add(entry.Mtime.ToLocalTime().ToString("yyyy-MM-dd HH:mm")); // 修改时间
                item.Tag = new ArchiveEntryTag(entry.Name, entry.Size, entry.IsDir, false);
                _fileList.Items.Add(item);
                _dirItemCount++;
                if (!entry.IsDir) _dirTotalSize += entry.Size;
            }
        }
        finally
        {
            _fileList.EndUpdate();
        }

        FitNameColumn();
        UpdateStatus();
    }

    /// <summary>切换为归档视图：记录归档路径 / 服务 / 条目，解压按钮启用。</summary>
    private void EnterArchiveMode(string path, IArchiveService service, List<ArchiveEntry> entries, string? password)
    {
        _archivePath = path;
        _archiveService = service;
        _archiveEntries = entries;
        _archivePassword = password; // 仅加密归档可能非 null（供解压/预览复用）
        _archiveEncrypted = entries.Any(e => e.IsEncrypted);
        UpdateOperationButtons();
        RefreshView();
        UpdateAddressBox();
    }

    /// <summary>退出归档视图（仅重置状态，由调用方负责刷新）。</summary>
    private void ExitArchiveMode()
    {
        if (_archivePath == null) return;
        _archivePath = null;
        _archiveService = null;
        _archiveEntries.Clear();
        _archivePassword = null;
        _archiveEncrypted = false;
        UpdateOperationButtons();
    }

    /// <summary>操作入口可用性：归档操作进行中全部禁用；空闲时解压仅归档视图可用。</summary>
    private void UpdateOperationButtons()
    {
        var idle = !_operationBusy;
        // 解压在「已打开归档」或「文件系统模式下选中一个归档文件」时可用
        var canExtract = (_archivePath != null || SelectedArchivePath() != null) && idle;
        _toolCompress.Enabled = idle;
        _toolOpenArchive.Enabled = idle;
        _compressItem.Enabled = idle;
        _openArchiveItem.Enabled = idle;
        _toolExtract.Enabled = canExtract;
        _extractItem.Enabled = canExtract;
    }

    // ---- 事件 ----

    private void OnFileListActivate(object? sender, EventArgs e)
    {
        if (_fileList.SelectedItems.Count == 0) return;

        if (_archivePath != null)
        {
            if (_fileList.SelectedItems[0].Tag is ArchiveEntryTag atag)
            {
                if (atag.IsUp) { GoUp(); return; }
                if (atag.IsDir) return;           // 目录不进入（迷你版不展开子目录）
                _ = PreviewArchiveEntry(atag);    // 文件双击 → 预览
            }
            return;
        }

        var tag = _fileList.SelectedItems[0].Tag as EntryTag;
        if (tag == null) return;
        if (tag.IsUp) { GoUp(); return; }
        if (tag.IsDir) { NavigateTo(tag.FullPath); return; }
        OpenFileViaShell(tag.FullPath); // 文件双击 → 系统默认方式打开
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

    // ---- 压缩 / 解压 / 打开归档 / 预览（全部后台执行 + 进度 + 取消）----

    private async void OnCompress()
    {
        if (_operationBusy) return;
        var sources = SelectedSourcePaths();
        using var dlg = new CompressDialog(sources);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var format = dlg.Format;
        var level = dlg.Level;
        var password = dlg.Password;

        var target = AskSaveTarget(format);
        if (target == null) return;

        var service = format == "mwr" ? (IArchiveService)new MwrService() : new ZipService();
        var result = await RunArchiveOperation("正在压缩...",
            (p, ct) => service.Compress(sources, target, level, password, p, ct));
        var error = result.Error;
        if (!result.Success)
        {
            if (error is not null) ShowOperationError(error); // 失败弹错；取消保持静默
            return;
        }

        RefreshView();
        MessageBox.Show(this,
            $"压缩完成：{result.Value!.EntryCount} 个条目，共 {FormatSize(result.Value.TotalSize)}。\n{target}",
            "压缩完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async void OnExtract()
    {
        if (_operationBusy) return;
        var archivePath = _archivePath ?? SelectedArchivePath();
        if (archivePath == null) return;
        // 归档模式用已知的加密标记；文件系统模式下探测归档是否加密，决定是否渲染密码框
        var needsPassword = _archivePath != null ? _archiveEncrypted : ArchiveProbe.IsEncrypted(archivePath);
        using var dlg = new ExtractDialog(needsPassword);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var targetDir = dlg.TargetDirectory;
        var password = dlg.Password ?? _archivePassword; // 未填则复用打开归档时的密码

        var service = _archiveService ?? CreateService(archivePath);
        var result = await RunArchiveOperation("正在解压...",
            (p, ct) => service.Extract(archivePath, targetDir, password, null, p, ct));
        var error = result.Error;
        if (!result.Success)
        {
            if (error is not null) ShowOperationError(error); // 失败弹错；取消保持静默
            return;
        }

        RefreshView();
        MessageBox.Show(this,
            $"解压完成：{result.Value!.EntryCount} 个条目，共 {FormatSize(result.Value.TotalSize)}。\n{targetDir}",
            "解压完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async void OnOpenArchive()
    {
        if (_operationBusy) return;
        string? path;
        using (var ofd = new OpenFileDialog
        {
            Title = "打开归档",
            Filter = "归档文件 (*.zip;*.mwr)|*.zip;*.mwr|所有文件 (*.*)|*.*",
            InitialDirectory = _currentDir,
        })
        {
            if (ofd.ShowDialog(this) != DialogResult.OK) return;
            path = ofd.FileName;
        }
        await OpenArchivePath(path);
    }

    /// <summary>打开归档（校验扩展名选择服务；密码错则提示重试）。</summary>
    private async Task OpenArchivePath(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext != ".zip" && ext != ".mwr")
        {
            MessageBox.Show(this, $"无法打开“{Path.GetFileName(path)}”：不是支持的归档格式（.zip / .mwr）。",
                "打开归档", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var service = CreateService(path);
        string? password = null;
        while (true)
        {
            var result = await RunArchiveOperation("正在打开归档...",
                (p, ct) => service.List(path, password));
            if (result.Success)
            {
                EnterArchiveMode(path, service, result.Value!, password);
                return;
            }
            if (result.Cancelled) return; // 用户取消打开，保持静默（状态栏已提示）
            if (result.Error is InvalidPasswordException)
            {
                password = PromptPassword(path);
                if (password == null) return; // 用户取消
                continue;
            }
            ShowOperationError(result.Error!);
            return;
        }
    }

    private async Task PreviewArchiveEntry(ArchiveEntryTag tag)
    {
        if (_operationBusy) return;
        if (_archivePath == null || _archiveService == null) return;
        var service = _archiveService;
        var archivePath = _archivePath;
        var password = _archivePassword;
        var result = await RunArchiveOperation("正在预览...",
            (p, ct) => service.Preview(archivePath, tag.EntryName, password));
        var error = result.Error;
        if (!result.Success)
        {
            if (error is not null) ShowOperationError(error); // 失败弹错；取消保持静默
            return;
        }
        ShowPreview(tag.EntryName, result.Value!);
    }

    /// <summary>
    /// 在后台线程执行归档操作：非模态 ProgressDialog 显示进度/取消，
    /// 完成后关闭并返回结果；失败返回异常（调用方决定提示或重试）。
    /// </summary>
    private async Task<OpResult<T>> RunArchiveOperation<T>(
        string progressTitle,
        Func<IProgress<ProgressInfo>, CancellationToken, T> operation)
    {
        _operationBusy = true;
        UpdateOperationButtons();
        try
        {
            using var progress = new ProgressDialog(progressTitle);
            progress.Show(this);
            try
            {
                var value = await Task.Run(() => operation(progress.Progress, progress.Token));
                progress.Complete();
                await Task.Delay(150); // 让"完成"状态短暂可见再关闭
                CloseProgressDialog(progress);
                return OpResult<T>.Ok(value);
            }
            catch (OperationCanceledException)
            {
                CloseProgressDialog(progress);
                _statusInfo.Text = "操作已取消。";
                return OpResult<T>.Cancel();
            }
            catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
            {
                CloseProgressDialog(progress);
                return OpResult<T>.Fail(e);
            }
        }
        finally
        {
            _operationBusy = false;
            UpdateOperationButtons();
        }
    }

    /// <summary>安全关闭进度对话框：操作期间用户点 X 关闭后，再次 Close 会抛 ObjectDisposedException。</summary>
    private static void CloseProgressDialog(ProgressDialog p)
    {
        if (p.IsDisposed) return;
        try { p.Close(); }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
        {
            // 对话框已在关闭中/已销毁，忽略
        }
    }

    /// <summary>错误统一转友好的中文提示（密码/损坏/取消等分开处理）。</summary>
    private void ShowOperationError(Exception e)
    {
        var (title, message) = e switch
        {
            InvalidPasswordException => ("密码错误", e.Message),
            ArchiveCorruptedException => ("归档文件已损坏", e.Message),
            FileNotFoundException => ("文件不存在", e.Message),
            UnauthorizedAccessException => ("访问被拒绝", e.Message),
            IOException => ("输入/输出错误", e.Message),
            _ => ("操作失败", e.Message),
        };
        MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void ShowPreview(string entryName, PreviewResult preview)
    {
        switch (preview.Kind)
        {
            case "text":
                ShowTextPreview(entryName, preview.Text ?? string.Empty);
                break;
            case "image" when preview.Bytes is { Length: > 0 }:
                OpenImageViaShell(entryName, preview.Bytes!); // 落临时文件交给系统查看器
                break;
            default:
                MessageBox.Show(this, $"“{entryName}” 是二进制文件，无法预览。", "预览",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                break;
        }
    }

    private void ShowTextPreview(string entryName, string text)
    {
        using var form = new Form
        {
            Text = "预览 — " + entryName,
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(720, 520),
            MinimumSize = new Size(400, 300),
        };
        var box = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Text = text,
            Font = new Font("Consolas", 10f),
        };
        form.Controls.Add(box);
        form.ShowDialog(this);
    }

    private void OpenImageViaShell(string entryName, byte[] bytes)
    {
        var ext = Path.GetExtension(entryName);
        if (string.IsNullOrEmpty(ext)) ext = ".img";
        var tmp = Path.Combine(Path.GetTempPath(), "MiniWinRAR_" + Guid.NewGuid().ToString("N") + ext);
        try
        {
            File.WriteAllBytes(tmp, bytes);
            Process.Start(new ProcessStartInfo(tmp) { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, $"无法预览图片：{e.Message}", "预览",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenFileViaShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            MessageBox.Show(this, $"无法打开文件：{e.Message}", "Mini-WinRAR",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>询问压缩目标文件；用户取消返回 null。</summary>
    private string? AskSaveTarget(string format)
    {
        var isMwr = format == "mwr";
        using var sfd = new SaveFileDialog
        {
            Title = isMwr ? "压缩为 Mini-WinRAR 归档" : "压缩为 ZIP 压缩文件",
            Filter = isMwr ? "Mini-WinRAR 归档 (*.mwr)|*.mwr" : "ZIP 压缩文件 (*.zip)|*.zip",
            AddExtension = true,
            DefaultExt = isMwr ? "mwr" : "zip",
            OverwritePrompt = true,
            InitialDirectory = _currentDir,
            FileName = SuggestArchiveName(isMwr ? "mwr" : "zip"),
        };
        return sfd.ShowDialog(this) == DialogResult.OK ? sfd.FileName : null;
    }

    /// <summary>按选中项/当前目录建议默认归档名（如 "Documents.zip"）。</summary>
    private string SuggestArchiveName(string ext)
    {
        foreach (ListViewItem i in _fileList.SelectedItems)
        {
            if (i.Tag is EntryTag tag && !tag.IsUp && !tag.IsDir)
                return Path.GetFileNameWithoutExtension(tag.Name) + "." + ext;
        }
        var dirName = Path.GetFileName(_currentDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return (string.IsNullOrEmpty(dirName) ? "archive" : dirName) + "." + ext;
    }

    /// <summary>压缩源：选中的文件/目录；无选择时压缩当前目录本身。</summary>
    private List<string> SelectedSourcePaths()
    {
        var paths = new List<string>();
        foreach (ListViewItem i in _fileList.SelectedItems)
        {
            if (i.Tag is EntryTag tag && !tag.IsUp) paths.Add(tag.FullPath);
        }
        if (paths.Count == 0) paths.Add(_currentDir);
        return paths;
    }

    /// <summary>文件系统模式下，选中项恰好是一个 .zip/.mwr 文件时返回其完整路径；否则返回 null（归档模式下由打开路径接管）。</summary>
    private string? SelectedArchivePath()
    {
        if (_archivePath != null) return null;
        if (_fileList.SelectedItems.Count != 1) return null;
        if (_fileList.SelectedItems[0].Tag is not EntryTag tag || tag.IsUp || tag.IsDir) return null;
        return ArchivePath.IsArchive(tag.FullPath) ? tag.FullPath : null;
    }

    private static IArchiveService CreateService(string path)
        => Path.GetExtension(path).Equals(".mwr", StringComparison.OrdinalIgnoreCase)
            ? new MwrService()
            : new ZipService();

    /// <summary>简单密码输入对话框；用户取消返回 null。</summary>
    private string? PromptPassword(string archivePath)
    {
        using var form = new Form
        {
            Text = "输入密码",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            Size = new Size(430, 160),
            MinimumSize = new Size(400, 150),
        };

        var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 4), ColumnCount = 2, RowCount = 2 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var label = new Label
        {
            Text = $"“{Path.GetFileName(archivePath)}” 已加密，请输入密码:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 8, 8),
        };
        var box = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true, Margin = new Padding(0, 0, 0, 8) };
        table.Controls.Add(label, 0, 0);
        table.Controls.Add(box, 1, 0);

        var ok = new Button { Text = "确定(&O)", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消(&C)", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Anchor = AnchorStyles.None;
        cancel.Anchor = AnchorStyles.None;
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        var row = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 46, ColumnCount = 3, RowCount = 1, Padding = new Padding(12, 8, 12, 8) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(cancel, 1, 0);
        row.Controls.Add(ok, 2, 0);

        form.Controls.Add(table);
        form.Controls.Add(row);
        return form.ShowDialog(this) == DialogResult.OK ? box.Text : null;
    }

    // ---- 拖拽 ----

    private static void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        var first = files[0];

        if (Directory.Exists(first))
        {
            NavigateTo(first); // 拖入文件夹 → 进入
            return;
        }
        if (File.Exists(first))
        {
            var ext = Path.GetExtension(first).ToLowerInvariant();
            if (ext == ".zip" || ext == ".mwr")
            {
                if (_operationBusy) return; // 操作进行中忽略拖入归档，避免覆盖视图
                _ = OpenArchivePath(first); // 拖入归档 → 打开归档视图
                return;
            }
            var dir = Path.GetDirectoryName(first);
            if (!string.IsNullOrEmpty(dir))
            {
                NavigateTo(dir); // 普通文件 → 进入其所在目录并选中
                SelectFile(first);
            }
            return;
        }
    }

    private void SelectFile(string fullPath)
    {
        if (_archivePath != null) return;
        foreach (ListViewItem i in _fileList.Items)
        {
            if (i.Tag is EntryTag tag && !tag.IsUp &&
                string.Equals(tag.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                _fileList.SelectedItems.Clear();
                i.Selected = true;
                i.Focused = true;
                _fileList.EnsureVisible(i.Index);
                return;
            }
        }
    }

    // ---- 辅助 ----

    /// <summary>ListView 条目附带的数据（文件系统视图：真实路径 + 是否为目录/上级）。</summary>
    private sealed record EntryTag(string Name, string FullPath, bool IsDir, bool IsUp, long Size);

    /// <summary>ListView 条目附带的数据（归档视图：归档内条目名 + 元信息）。</summary>
    private sealed record ArchiveEntryTag(string EntryName, long Size, bool IsDir, bool IsUp);

    /// <summary>后台归档操作的结果：成功值 / 失败异常 / 用户取消。</summary>
    private sealed record OpResult<T>(T? Value, Exception? Error, bool Cancelled)
    {
        public static OpResult<T> Ok(T value) => new(value, null, false);
        public static OpResult<T> Fail(Exception error) => new(default, error, false);
        public static OpResult<T> Cancel() => new(default, null, true);
        public bool Success => Error is null && !Cancelled;
    }

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
