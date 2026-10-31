using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PhotoOrganizer;

internal sealed class MainForm : Form
{
    private readonly string dataDirectory;
    private readonly AppSettings settings;
    private readonly IWindowDialogs dialogs;
    private readonly TextCatalog text = new();
    private readonly List<Action> translations = [];
    private readonly TextBox source = new() { Dock = DockStyle.Fill, AllowDrop = true };
    private readonly TextBox destination = new() { Dock = DockStyle.Fill, AllowDrop = true };
    private readonly ComboBox language = Choice(140);
    private readonly ComboBox theme = Choice(155);
    private readonly ComboBox naming = Choice(240);
    private readonly ComboBox mode = Choice(175);
    private readonly ComboBox scope = Choice(190);
    private readonly ComboBox filter = Choice(160);
    private readonly TextBox search = new() { Width = 240 };
    private readonly CheckBox videos = new() { AutoSize = true };
    private readonly Button scan = new ThemeButton() { AutoSize = true };
    private readonly Button copy = new ThemeButton() { AutoSize = true };
    private readonly Button cancel = new ThemeButton() { AutoSize = true, Enabled = false };
    private readonly Button historyButton = new ThemeButton() { AutoSize = true };
    private readonly Button undoButton = new ThemeButton() { AutoSize = true, Visible = false };
    private readonly Button recoverButton = new ThemeButton() { AutoSize = true, Visible = false };
    private readonly Button exportButton = new ThemeButton() { AutoSize = true, Visible = false };
    private readonly Button clearButton = new ThemeButton() { AutoSize = true, Visible = false };
    private readonly Button abandonButton = new ThemeButton() { AutoSize = true, Visible = false };
    private readonly Button diagnosticsButton = new ThemeButton() { AutoSize = true, Visible = false };
    private readonly Button helpButton = new ThemeButton() { AutoSize = true };
    private readonly Button locateButton = new ThemeButton() { AutoSize = true };
    private readonly Label summary = new() { AutoSize = true };
    private readonly Label status = new() { AutoSize = true };
    private readonly Label eta = new() { AutoSize = true };
    private readonly Label currentFile = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly ProgressBar progressBar = new() { Dock = DockStyle.Fill, Height = 14, Maximum = 1000 };
    private readonly DataGridView grid = NewGrid();
    private readonly DataGridView historyGrid = NewGrid();
    private readonly Panel content = new() { Dock = DockStyle.Fill };
    private readonly Label historyNote = new() { Dock = DockStyle.Bottom, Height = 56, Padding = new(8) };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    private readonly List<Control> optionControls = [];
    private List<PlanItem> plan = [];
    private List<PlanItem> visibleItems = [];
    private ScanOptions? previewOptions;
    private CancellationTokenSource? cancellation;
    private RemainingTime remaining = new();
    private WorkProgress? latestProgress;
    private bool building;
    private bool showingHistory;
    private string statusKey = "Idle";
    private List<HistoryAction>? undoPreview;
    private List<HistoryEntry> historyEntries = [];
    private readonly PictureBox thumbnail = new() { Size = new(130, 82), SizeMode = PictureBoxSizeMode.Zoom };
    private readonly Label photoDetails = new() { AutoSize = true, MaximumSize = new(1000, 0), Margin = new(14, 6, 0, 0) };
    private int thumbnailGeneration;

    public MainForm(string? storageDirectory = null, IWindowDialogs? windowDialogs = null)
    {
        dialogs = windowDialogs ?? new WindowDialogs();
        dataDirectory = storageDirectory ?? AppSettings.DataDirectory;
        settings = AppSettings.Load(dataDirectory);
        text.Load(settings.Language);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Font = new Font("Microsoft YaHei UI", 10);
        MinimumSize = new Size(1120, 760);
        Size = new Size(1400, 940);
        StartPosition = FormStartPosition.CenterScreen;
        BuildLayout();
        WireEvents();
        ApplyLanguage();
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
        Shown += (_, _) => CheckPendingHistory();
        FormClosing += (_, e) =>
        {
            if (cancellation is null) return;
            e.Cancel = true;
            dialogs.Notify(this, text["CloseWhileBusy"], text["ErrorTitle"]);
        };
    }

    private static ComboBox Choice(int width)
    {
        var box = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Width = width,
            DrawMode = DrawMode.OwnerDrawFixed, FlatStyle = FlatStyle.Flat, ItemHeight = 24
        };
        box.DrawItem += (_, e) =>
        {
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using var brush = new SolidBrush(selected ? SystemColors.Highlight : box.BackColor);
            e.Graphics.FillRectangle(brush, e.Bounds);
            var label = e.Index >= 0 ? box.Items[e.Index]?.ToString() ?? "" : box.Text;
            TextRenderer.DrawText(e.Graphics, label, box.Font, e.Bounds,
                selected ? SystemColors.HighlightText : box.ForeColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            e.DrawFocusRectangle();
        };
        return box;
    }
    private static DataGridView NewGrid() => new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, BorderStyle = BorderStyle.None,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false, RowTemplate = { Height = 46 }, EnableHeadersVisualStyles = false,
        ColumnHeadersHeight = 42, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
    };

    private Label LabelFor(string key)
    {
        var label = new Label { AutoSize = true, Margin = new(5, 9, 10, 5) };
        Bind(label, key);
        return label;
    }
    private void Bind(Control control, string key) => translations.Add(() =>
    {
        control.Text = text[key];
        control.Name = key;
        control.AccessibleName = text[key];
    });
    private static FlowLayoutPanel Flow() => new()
    { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new(0, 4, 0, 4) };

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, Padding = new(24, 16, 24, 16) };
        for (int row = 0; row < 9; row++)
            root.RowStyles.Add(new RowStyle(row == 6 ? SizeType.Percent : SizeType.AutoSize, row == 6 ? 100 : 0));
        Controls.Add(root);

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        header.ColumnStyles.Add(new(SizeType.Percent, 100));
        header.ColumnStyles.Add(new(SizeType.Absolute, 560));
        var titles = Flow();
        titles.FlowDirection = FlowDirection.TopDown;
        var title = LabelFor("Title");
        title.Font = new Font(Font.FontFamily, 24, FontStyle.Bold);
        titles.Controls.Add(title);
        titles.Controls.Add(LabelFor("Subtitle"));
        header.Controls.Add(titles, 0, 0);
        var settingsFlow = Flow();
        settingsFlow.AutoSize = false;
        settingsFlow.Height = 75;
        settingsFlow.Controls.Add(LabelFor("Language"));
        language.Name = "LanguageChoice";
        theme.Name = "ThemeChoice";
        language.Items.AddRange(["简体中文", "English"]);
        settingsFlow.Controls.Add(language);
        settingsFlow.Controls.Add(LabelFor("Theme"));
        settingsFlow.Controls.Add(theme);
        header.Controls.Add(settingsFlow, 1, 0);
        root.Controls.Add(header, 0, 0);

        var directories = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        directories.Name = "DirectoriesPanel";
        directories.ColumnStyles.Add(new(SizeType.Percent, 50));
        directories.ColumnStyles.Add(new(SizeType.Percent, 50));
        directories.Controls.Add(DirectoryCard("Source", source), 0, 0);
        directories.Controls.Add(DirectoryCard("Destination", destination), 1, 0);
        root.Controls.Add(directories, 0, 1);

        var options = Flow();
        options.Name = "OptionsPanel";
        Bind(videos, "Videos");
        options.Controls.Add(LabelFor("Operation"));
        options.Controls.Add(mode);
        options.Controls.Add(LabelFor("Scope"));
        options.Controls.Add(scope);
        options.Controls.Add(videos);
        options.Controls.Add(LabelFor("Naming"));
        options.Controls.Add(naming);
        options.Controls.Add(LabelFor("RuleNote"));
        root.Controls.Add(options, 0, 2);
        root.Controls.Add(LabelFor("PreviewNotice"), 0, 3);

        var toolbar = Flow();
        foreach (var pair in new[] { (scan, "Preview"), (copy, "Copy"), (cancel, "Cancel"), (historyButton, "History"),
            (undoButton, "UndoPreview"), (recoverButton, "Recover"), (exportButton, "ExportHistory"),
            (clearButton, "ClearHistory"), (abandonButton, "Abandon"), (diagnosticsButton, "Diagnostics") })
        {
            Bind(pair.Item1, pair.Item2);
            pair.Item1.Padding = new(12, 7, 12, 7);
            pair.Item1.FlatStyle = FlatStyle.Flat;
            toolbar.Controls.Add(pair.Item1);
        }
        root.Controls.Add(toolbar, 0, 4);
        var filters = Flow();
        filters.Controls.Add(filter);
        filters.Controls.Add(search);
        filters.Controls.Add(summary);
        root.Controls.Add(filters, 0, 5);

        grid.VirtualMode = true;
        grid.Name = "PreviewGrid";
        historyGrid.Name = "HistoryGrid";
        search.Name = "SearchBox";
        mode.Name = "OperationChoice";
        scope.Name = "ScopeChoice";
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Select", FillWeight = 7 });
        AddColumn(grid, "OriginalFile", 25);
        AddColumn(grid, "TargetFile", 43);
        AddColumn(grid, "CaptureDate", 21);
        AddColumn(grid, "DateSource", 20);
        AddColumn(grid, "Status", 20);
        AddColumn(grid, "Detail", 20);
        historyGrid.ReadOnly = true;
        historyGrid.VirtualMode = true;
        historyGrid.MultiSelect = true;
        foreach (var key in new[] { "Created", "OriginalFile", "TargetFile", "JournalState", "Detail" })
            AddColumn(historyGrid, key, key is "OriginalFile" or "TargetFile" ? 30 : 18);
        historyGrid.Visible = false;
        historyNote.Visible = false;
        Bind(historyNote, "HistoryNote");
        content.Controls.Add(grid);
        content.Controls.Add(historyGrid);
        content.Controls.Add(historyNote);
        root.Controls.Add(content, 0, 6);
        var detailPanel = Flow();
        detailPanel.Name = "PhotoDetailPanel";
        photoDetails.Name = "PhotoDetails";
        detailPanel.Controls.Add(thumbnail);
        detailPanel.Controls.Add(photoDetails);
        root.Controls.Add(detailPanel, 0, 7);
        Resize += (_, _) => detailPanel.Visible = !showingHistory && ClientSize.Height >= 800;

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Padding = new(0, 10, 0, 0) };
        footer.Controls.Add(currentFile);
        footer.Controls.Add(progressBar);
        var progressLabels = Flow();
        progressLabels.Controls.Add(status);
        eta.Margin = new(20, 9, 0, 5);
        progressLabels.Controls.Add(eta);
        Bind(helpButton, "HelpPrivacy");
        progressLabels.Controls.Add(helpButton);
        Bind(locateButton, "Locate");
        progressLabels.Controls.Add(locateButton);
        footer.Controls.Add(progressLabels);
        root.Controls.Add(footer, 0, 8);
        optionControls.AddRange([source, destination, mode, scope, videos, naming, scan, search, filter, historyButton, grid,
            undoButton, recoverButton, exportButton, clearButton, abandonButton, diagnosticsButton, historyGrid, locateButton]);
    }

    private Control DirectoryCard(string key, TextBox field)
    {
        var panel = new TableLayoutPanel
        { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new(8, 12, 12, 12) };
        panel.ColumnStyles.Add(new(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new(SizeType.AutoSize));
        panel.Controls.Add(LabelFor(key), 0, 0);
        panel.SetColumnSpan(panel.GetControlFromPosition(0, 0)!, 2);
        field.Margin = new(4, 10, 10, 4);
        field.AccessibleName = text[key];
        field.Name = key + "Path";
        translations.Add(() => field.AccessibleName = text[key]);
        var browse = new ThemeButton { AutoSize = true, Padding = new(8, 3, 8, 3) };
        Bind(browse, "Browse");
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = text[key], UseDescriptionForTitle = true };
            if (Directory.Exists(field.Text)) dialog.SelectedPath = field.Text;
            if (dialog.ShowDialog(this) == DialogResult.OK) field.Text = dialog.SelectedPath;
        };
        field.DragEnter += (_, e) => e.Effect =
            e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        field.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths && Directory.Exists(paths[0]))
                field.Text = paths[0];
        };
        panel.Controls.Add(field, 0, 1);
        panel.Controls.Add(browse, 1, 1);
        optionControls.Add(browse);
        return panel;
    }

    private static void AddColumn(DataGridView view, string key, float weight) =>
        view.Columns.Add(new DataGridViewTextBoxColumn
        { Name = key, FillWeight = weight, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });

    private void WireEvents()
    {
        language.SelectedIndexChanged += (_, _) =>
        {
            if (building) return;
            settings.Language = language.SelectedIndex == 1 ? "en" : "zh-CN";
            text.Load(settings.Language);
            ApplyLanguage();
            SaveSettings();
        };
        theme.SelectedIndexChanged += (_, _) =>
        {
            if (building) return;
            settings.Theme = (ThemeMode)theme.SelectedIndex;
            ApplyTheme();
            SaveSettings();
        };
        source.TextChanged += (_, _) => InvalidatePreview();
        destination.TextChanged += (_, _) => InvalidatePreview();
        videos.CheckedChanged += (_, _) => InvalidatePreview();
        naming.SelectedIndexChanged += (_, _) => { if (!building) InvalidatePreview(); };
        mode.SelectedIndexChanged += (_, _) => { if (!building) { InvalidatePreview(); UpdateOperationUi(); } };
        scope.SelectedIndexChanged += (_, _) => { if (!building) InvalidatePreview(); };
        filter.SelectedIndexChanged += (_, _) => { if (!building) RefreshRows(); };
        search.TextChanged += (_, _) => RefreshRows();
        scan.Click += async (_, _) => await ScanAsync();
        copy.Click += async (_, _) => await CopyAsync();
        cancel.Click += (_, _) => cancellation?.Cancel();
        historyButton.Click += (_, _) => ToggleHistory();
        historyGrid.CellValueNeeded += (_, e) =>
        {
            int count = undoPreview?.Count ?? historyEntries.Count;
            if (e.RowIndex < 0 || e.RowIndex >= count) return;
            var entry = HistoryEntryAt(e.RowIndex);
            var action = undoPreview?[e.RowIndex];
            e.Value = e.ColumnIndex switch
            {
                0 => DateTimeOffset.TryParse(entry.Created, out var created) ? created.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") : entry.Created,
                1 => action?.From ?? entry.Source,
                2 => action?.To ?? entry.Destination,
                3 => text[action is null ? entry.State : action.CanExecute ? "Ready" : "RecoveryConflict"],
                _ => text[action?.Reason ?? entry.Detail]
            };
        };
        undoButton.Click += async (_, _) => await UndoAsync();
        recoverButton.Click += async (_, _) =>
        {
            if (!dialogs.Confirm(this, text["ConfirmRecover"], text["Recover"])) return;
            await RunTaskAsync((reporter, token) => Task.Run(() => HistoryService.RecoverAsync(dataDirectory, reporter, token), token), "Recovering");
            LoadHistory();
        };
        exportButton.Click += (_, _) => ExportHistory();
        clearButton.Click += (_, _) =>
        {
            if (!dialogs.Confirm(this, text["ConfirmClear"], text["ClearHistory"])) return;
            try { using var journal = new Journal(dataDirectory); journal.Clear(); LoadHistory(); }
            catch (Exception ex) { dialogs.Notify(this, text[ex.Message], text["ErrorTitle"]); }
        };
        abandonButton.Click += (_, _) =>
        {
            if (!dialogs.Confirm(this, text["ConfirmAbandon"], text["Abandon"])) return;
            try { HistoryService.Abandon(dataDirectory, SelectedHistoryIds()); LoadHistory(); }
            catch (Exception ex) { dialogs.Notify(this, text[ex.Message], text["ErrorTitle"]); }
        };
        diagnosticsButton.Click += (_, _) =>
        {
            using var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "photo-organizer-diagnostics.json" };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                try { File.WriteAllText(dialog.FileName, Diagnostics.Create(settings)); }
                catch (Exception ex) { dialogs.Notify(this, text[ex.Message], text["ErrorTitle"]); }
            }
        };
        helpButton.Click += (_, _) =>
        {
            using var window = new Form { Text = text["HelpPrivacy"], Size = new(820, 650),
                StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false };
            window.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
                ScrollBars = ScrollBars.Vertical, Font = Font, Text = File.ReadAllText(
                    Path.Combine(AppContext.BaseDirectory, "Docs", "privacy." + text.Language + ".txt")) +
                    Environment.NewLine + File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Docs", "third-party.txt")) });
            ApplyTheme(window);
            window.ShowDialog(this);
        };
        locateButton.Click += (_, _) =>
        {
            IEnumerable<string> candidates = [];
            if (showingHistory && historyGrid.CurrentCell is { } cell && cell.RowIndex < (undoPreview?.Count ?? historyEntries.Count))
            {
                var entry = HistoryEntryAt(cell.RowIndex);
                candidates = [entry.UndoDestination, entry.Destination, entry.Source];
            }
            else if (!showingHistory && grid.CurrentCell is { } current && current.RowIndex < visibleItems.Count)
            {
                var item = visibleItems[current.RowIndex];
                candidates = [item.Destination, item.Source];
            }
            string? path = candidates.FirstOrDefault(path => File.Exists(path) || Directory.Exists(path));
            if (path is null) { dialogs.Notify(this, text["NothingSelected"], text["Locate"]); return; }
            try { FileLocator.Open(path); }
            catch (Exception ex) { dialogs.Notify(this, text[ex.Message], text["ErrorTitle"]); }
        };
        grid.SelectionChanged += async (_, _) => await UpdatePhotoDetailsAsync();
        grid.CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= visibleItems.Count) return;
            var item = visibleItems[e.RowIndex];
            e.Value = e.ColumnIndex switch
            {
                0 => item.Selected,
                1 => Path.GetRelativePath(source.Text, item.Source),
                2 => string.IsNullOrEmpty(item.Destination) ? "—" : Path.GetRelativePath(previewOptions?.Destination ?? destination.Text, item.Destination),
                3 => item.CapturedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—",
                4 => text[item.DateSource],
                5 => text[item.State.ToString()],
                _ => text[item.Detail]
            };
        };
        grid.CellBeginEdit += (_, e) =>
        {
            if (e.ColumnIndex != 0 || !IsCopyCandidate(visibleItems[e.RowIndex])) e.Cancel = true;
        };
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        grid.CellValuePushed += (_, e) =>
        {
            if (e.ColumnIndex != 0 || e.RowIndex < 0 || e.RowIndex >= visibleItems.Count) return;
            var item = visibleItems[e.RowIndex];
            if (IsCopyCandidate(item)) item.Selected = e.Value is true;
            UpdateSummary();
        };
        grid.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.RowIndex < visibleItems.Count)
                e.ToolTipText = visibleItems[e.RowIndex].Source + Environment.NewLine + visibleItems[e.RowIndex].Destination +
                    Environment.NewLine + text[visibleItems[e.RowIndex].Detail];
        };
        timer.Tick += (_, _) => UpdateProgress();
    }

    private static bool IsCopyCandidate(PlanItem item) => item.State is ItemState.Ready or ItemState.Fallback;
    private void SaveSettings()
    {
        try { settings.Save(dataDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { dialogs.Notify(this, text["StorageWarning"] + Environment.NewLine + ex.Message, text["ErrorTitle"]); }
    }

    private void ApplyLanguage()
    {
        building = true;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(text.Language);
            Text = text["App"];
            foreach (var translate in translations) translate();
            language.SelectedIndex = settings.Language == "en" ? 1 : 0;
            ReplaceChoices(theme, ["System", "Light", "Dark"], (int)settings.Theme);
            ReplaceChoices(naming, ["DateDevice", "Original", "Legacy"], Math.Max(0, naming.SelectedIndex));
            ReplaceChoices(mode, ["CopyMode", "RenameMode"], Math.Max(0, mode.SelectedIndex));
            ReplaceChoices(scope, ["AllSubfolders", "ThisFolder", "YearFolders"], Math.Max(0, scope.SelectedIndex));
            ReplaceChoices(filter, ["FilterAll", "FilterReview", "FilterSkipped", "FilterFailed"], Math.Max(0, filter.SelectedIndex));
            foreach (var view in new[] { grid, historyGrid })
                foreach (DataGridViewColumn column in view.Columns) column.HeaderText = text[column.Name];
            search.PlaceholderText = text["Search"];
            search.AccessibleName = text["Search"];
            historyButton.Text = text[showingHistory ? "Back" : "History"];
            historyButton.AccessibleName = historyButton.Text;
            if (latestProgress is null) status.Text = text[statusKey];
            else UpdateProgress();
            if (showingHistory) LoadHistory();
        }
        finally { building = false; }
        RefreshRows();
        UpdateOperationUi();
        _ = UpdatePhotoDetailsAsync();
    }

    private void ReplaceChoices(ComboBox box, string[] keys, int index)
    {
        box.Items.Clear();
        box.Items.AddRange(keys.Select(key => (object)text[key]).ToArray());
        box.SelectedIndex = index;
    }

    private void InvalidatePreview()
    {
        if (building || cancellation is not null) return;
        plan.Clear();
        previewOptions = null;
        RefreshRows();
        latestProgress = null;
        progressBar.Value = 0;
        statusKey = "Idle";
        status.Text = text["Idle"];
        currentFile.Text = "";
        eta.Text = "";
    }

    private void RefreshRows()
    {
        visibleItems = plan.Where(item => filter.SelectedIndex switch
        {
            1 => item.State == ItemState.Fallback,
            2 => item.State is ItemState.Unsupported or ItemState.Duplicate or ItemState.Companion or ItemState.Unchanged,
            3 => item.State == ItemState.Failed,
            _ => true
        }).Where(item => string.IsNullOrEmpty(search.Text) ||
            Path.GetFileName(item.Source).Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToList();
        grid.RowCount = visibleItems.Count;
        grid.Invalidate();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (showingHistory)
        {
            summary.Text = undoPreview is null ?
                text.Format("HistorySummary", historyEntries.Count, historyEntries.Count(x => x.State is "Planned" or "Staged" or "UndoPlanned")) :
                text.Format("UndoSummary", undoPreview.Count(x => x.CanExecute), undoPreview.Count(x => !x.CanExecute));
            copy.Enabled = false;
            return;
        }
        var selected = plan.Where(item => item.Selected && IsCopyCandidate(item)).ToList();
        summary.Text = text.Format("SelectionSummary", plan.Count, selected.Count, FormatSize(selected.Sum(x => x.Length)));
        copy.Enabled = cancellation is null && previewOptions is not null && selected.Count > 0 && !showingHistory;
    }
    internal static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024 * 1024):F2} GB";
        if (bytes >= 1024 * 1024) return $"{bytes / (1024d * 1024):F1} MB";
        return $"{bytes / 1024d:F1} KB";
    }

    private ScanOptions Options() => new(source.Text.Trim(),
        mode.SelectedIndex == 1 ? source.Text.Trim() : destination.Text.Trim(), scope.SelectedIndex == 0,
        videos.Checked, (NamingRule)Math.Max(0, naming.SelectedIndex),
        (OperationMode)Math.Max(0, mode.SelectedIndex), scope.SelectedIndex == 2);

    private async Task ScanAsync()
    {
        if (showingHistory) ToggleHistory();
        var options = Options();
        plan = [];
        previewOptions = null;
        RefreshRows();
        await RunTaskAsync(async (reporter, token) =>
        {
            var result = await Task.Run(() => Planner.ScanAsync(options, reporter, token), token);
            plan = result;
            previewOptions = options;
            RefreshRows();
        }, "Enumerating");
    }

    private async Task CopyAsync()
    {
        grid.EndEdit();
        var items = plan.Where(item => item.Selected && IsCopyCandidate(item)).ToList();
        if (previewOptions is null || items.Count == 0) return;
        string confirmKey = previewOptions.Mode == OperationMode.Rename ? "ConfirmRename" : "ConfirmCopy";
        if (!dialogs.Confirm(this, text.Format(confirmKey, items.Count, FormatSize(items.Sum(x => x.Length)),
                previewOptions.Destination), text["ConfirmTitle"])) return;
        var options = previewOptions;
        await RunTaskAsync((reporter, token) => Task.Run(() =>
            options.Mode == OperationMode.Rename ?
                RenameExecutor.ExecuteAsync(options, items, dataDirectory, reporter, token) :
                CopyExecutor.ExecuteAsync(options, items, dataDirectory, reporter, token), token),
            options.Mode == OperationMode.Rename ? "Renaming" : "Copying");
        RefreshRows();
        summary.Text = text.Format("Results", plan.Count(x => x.State is ItemState.Copied or ItemState.Renamed),
            plan.Count(x => x.State == ItemState.Failed), plan.Count(IsCopyCandidate));
    }

    private async Task RunTaskAsync(Func<IProgress<WorkProgress>, CancellationToken, Task> action, string stage)
    {
        cancellation = new();
        var taskCancellation = cancellation;
        remaining = new();
        latestProgress = new(stage, 0, 0, 0, 0, "");
        foreach (var control in optionControls) control.Enabled = false;
        copy.Enabled = false;
        cancel.Enabled = true;
        timer.Start();
        bool wasCanceled = false;
        bool failed = false;
        var reporter = new Progress<WorkProgress>(value =>
        {
            if (cancellation != taskCancellation) return;
            if (latestProgress?.Stage != value.Stage) remaining = new();
            latestProgress = value;
            UpdateProgress();
        });
        try { await action(reporter, cancellation.Token); }
        catch (OperationCanceledException) { wasCanceled = true; }
        catch (Exception ex)
        {
            failed = true;
            dialogs.Notify(this, text[ex.Message], text["ErrorTitle"]);
        }
        finally
        {
            timer.Stop();
            cancellation.Dispose();
            cancellation = null;
            foreach (var control in optionControls) control.Enabled = true;
            cancel.Enabled = false;
            progressBar.Style = ProgressBarStyle.Continuous;
            if (!wasCanceled && !failed) progressBar.Value = 1000;
            statusKey = wasCanceled ? "CanceledSummary" : failed ? "ErrorTitle" : "Finished";
            status.Text = text[statusKey];
            eta.Text = "";
            currentFile.Text = "";
            latestProgress = null;
            UpdateSummary();
            UpdateOperationUi();
        }
    }

    private void UpdateProgress()
    {
        if (latestProgress is not { } value) return;
        currentFile.Text = value.FileName;
        if (value.Total == 0)
        {
            progressBar.Style = ProgressBarStyle.Marquee;
            status.Text = value.Stage == "Enumerating" ? text.Format("Enumerating", value.Completed) : text[value.Stage];
            eta.Text = text["Estimating"];
            return;
        }
        progressBar.Style = ProgressBarStyle.Continuous;
        var fraction = value.TotalUnits == 0 ? (double)value.Completed / value.Total :
            Math.Clamp((double)value.Units / value.TotalUnits, 0, 1);
        progressBar.Value = (int)(fraction * 1000);
        status.Text = text.Format("Progress", text[value.Stage], value.Completed, value.Total, fraction);
        var estimate = remaining.Estimate(value.TotalUnits == 0 ? value.Completed : value.Units,
            value.TotalUnits == 0 ? value.Total : value.TotalUnits);
        eta.Text = estimate is null ? text["Estimating"] : text.Format("Remaining",
            estimate.Value.TotalDays >= 1 ? estimate.Value.ToString(@"d\.hh\:mm\:ss") : estimate.Value.ToString(@"hh\:mm\:ss"));
    }

    private void ToggleHistory()
    {
        showingHistory = !showingHistory;
        historyGrid.Visible = showingHistory;
        historyNote.Visible = showingHistory;
        grid.Visible = !showingHistory;
        historyButton.Text = text[showingHistory ? "Back" : "History"];
        historyButton.AccessibleName = historyButton.Text;
        foreach (string name in new[] { "DirectoriesPanel", "OptionsPanel", "PreviewNotice" })
            Controls.Find(name, true).Single().Visible = !showingHistory;
        scan.Visible = !showingHistory;
        copy.Visible = !showingHistory;
        filter.Visible = !showingHistory;
        search.Visible = !showingHistory;
        Controls.Find("PhotoDetailPanel", true).Single().Visible = !showingHistory && ClientSize.Height >= 800;
        UpdateOperationUi();
        foreach (var button in new[] { undoButton, recoverButton, exportButton, clearButton, abandonButton, diagnosticsButton })
            button.Visible = showingHistory;
        if (showingHistory) LoadHistory();
        UpdateSummary();
    }
    private void LoadHistory()
    {
        try
        {
            using var journal = new Journal(dataDirectory);
            undoPreview = null;
            undoButton.Text = text["UndoPreview"];
            undoButton.AccessibleName = undoButton.Text;
            historyGrid.Columns[2].HeaderText = text["TargetFile"];
            historyNote.Text = text["HistoryNote"];
            historyNote.AccessibleName = historyNote.Text;
            historyEntries = journal.Read();
            historyGrid.RowCount = historyEntries.Count;
            historyGrid.Invalidate();
            UpdateSummary();
        }
        catch (Exception ex) { dialogs.Notify(this, text["HistoryError"] + Environment.NewLine + ex.Message, text["ErrorTitle"]); }
    }
    private void CheckPendingHistory()
    {
        try
        {
            using var journal = new Journal(dataDirectory);
            int count = journal.PendingCount();
            if (count > 0) dialogs.Notify(this, text.Format("PendingWarning", count), text["History"]);
        }
        catch (Exception ex) { dialogs.Notify(this, text["HistoryError"] + Environment.NewLine + ex.Message, text["ErrorTitle"]); }
    }

    private void UpdateOperationUi()
    {
        var title = Controls.Find("Title", true).SingleOrDefault();
        if (title is not null) { title.Text = text[showingHistory ? "History" : "Title"]; title.AccessibleName = title.Text; }
        var subtitle = Controls.Find("Subtitle", true).SingleOrDefault();
        if (subtitle is not null) { subtitle.Text = text[showingHistory ? "HistorySubtitle" : "Subtitle"]; subtitle.AccessibleName = subtitle.Text; }
        bool rename = mode.SelectedIndex == 1;
        bool editable = cancellation is null;
        destination.Enabled = editable && !rename;
        foreach (var button in destination.Parent!.Controls.OfType<Button>()) button.Enabled = editable && !rename;
        copy.Text = text[rename ? "StartRename" : "Copy"];
        copy.AccessibleName = copy.Text;
        var notice = Controls.Find("PreviewNotice", true).SingleOrDefault();
        if (notice is not null) { notice.Text = text[rename ? "RenameNotice" : "PreviewNotice"]; notice.AccessibleName = notice.Text; }
        var rule = Controls.Find("RuleNote", true).SingleOrDefault();
        if (rule is not null) { rule.Text = text[rename ? "RenameRuleNote" : "RuleNote"]; rule.AccessibleName = rule.Text; }
    }

    private HistoryEntry HistoryEntryAt(int index) => undoPreview is null ? historyEntries[index] : undoPreview[index].Entry;
    private long[] SelectedHistoryIds() => historyGrid.SelectedRows.Cast<DataGridViewRow>()
        .Select(row => HistoryEntryAt(row.Index).Id).ToArray();

    private async Task UndoAsync()
    {
        if (undoPreview is not null)
        {
            var selected = SelectedHistoryIds().ToHashSet();
            var actions = undoPreview.Where(action => action.CanExecute && selected.Contains(action.Entry.Id)).ToList();
            if (actions.Count == 0) { dialogs.Notify(this, text["NothingSelected"], text["UndoPreview"]); return; }
            if (!dialogs.Confirm(this, text.Format("ConfirmUndo", actions.Count), text["UndoPreview"])) return;
            await RunTaskAsync((reporter, token) => Task.Run(() =>
                HistoryService.UndoAsync(dataDirectory, actions, reporter, token), token), "Undoing");
            LoadHistory();
            return;
        }
        var ids = SelectedHistoryIds();
        if (ids.Length == 0) { dialogs.Notify(this, text["NothingSelected"], text["UndoPreview"]); return; }
        List<HistoryAction>? preview = null;
        await RunTaskAsync(async (reporter, token) =>
            preview = await Task.Run(() => HistoryService.PreviewUndoAsync(dataDirectory, ids, token, reporter), token), "Undoing");
        if (preview is null) return;
        undoPreview = preview;
        historyGrid.Columns[2].HeaderText = text["UndoTargetFile"];
        historyGrid.RowCount = preview.Count;
        historyGrid.Invalidate();
        historyGrid.SelectAll();
        undoButton.Text = text["ExecuteUndo"];
        undoButton.AccessibleName = undoButton.Text;
        historyNote.Text = text["UndoPreviewNote"];
        historyNote.AccessibleName = historyNote.Text;
        UpdateSummary();
    }

    private void ExportHistory()
    {
        using var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "photo-organizer-history.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var ids = SelectedHistoryIds();
            HistoryService.Export(dataDirectory, dialog.FileName, ids.Length == 0 ? null : ids);
        }
        catch (Exception ex) { dialogs.Notify(this, text[ex.Message], text["ErrorTitle"]); }
    }

    private async Task UpdatePhotoDetailsAsync()
    {
        if (IsDisposed || Disposing) return;
        int generation = ++thumbnailGeneration;
        var previous = thumbnail.Image;
        thumbnail.Image = null;
        previous?.Dispose();
        photoDetails.Text = "";
        if (grid.CurrentCell is null || grid.CurrentCell.RowIndex >= visibleItems.Count) return;
        var item = visibleItems[grid.CurrentCell.RowIndex];
        photoDetails.Text = Path.GetFileName(item.Source) + Environment.NewLine +
            (item.CapturedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "") + " · " +
            text[item.DateSource] + " · " + item.Device + " · " + FormatSize(item.Length);
        string path = File.Exists(item.Source) ? item.Source : item.Destination;
        if (Path.GetExtension(path).ToLowerInvariant() is not (".jpg" or ".jpeg" or ".png")) return;
        Image? image = null;
        try
        {
            image = await Task.Run(() =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var original = Image.FromStream(stream);
                if ((long)original.Width * original.Height > 50_000_000) throw new ArgumentException("Thumbnail too large");
                var orientation = original.PropertyIdList.Contains(0x112) ? original.GetPropertyItem(0x112)?.Value : null;
                if (orientation?.Length >= 2)
                    original.RotateFlip(BitConverter.ToUInt16(orientation) switch
                    {
                        2 => RotateFlipType.RotateNoneFlipX, 3 => RotateFlipType.Rotate180FlipNone,
                        4 => RotateFlipType.Rotate180FlipX, 5 => RotateFlipType.Rotate90FlipX,
                        6 => RotateFlipType.Rotate90FlipNone, 7 => RotateFlipType.Rotate270FlipX,
                        8 => RotateFlipType.Rotate270FlipNone, _ => RotateFlipType.RotateNoneFlipNone
                    });
                double ratio = Math.Min(160d / original.Width, 100d / original.Height);
                return original.GetThumbnailImage(Math.Max(1, (int)(original.Width * ratio)),
                    Math.Max(1, (int)(original.Height * ratio)), null, IntPtr.Zero);
            });
            if (generation != thumbnailGeneration || IsDisposed) { image.Dispose(); return; }
            thumbnail.Image = image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
        { image?.Dispose(); }
    }

    private void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => ApplyTheme()); }
        catch (InvalidOperationException) { }
    }

    private void ApplyTheme(Control? target = null)
    {
        bool dark = settings.Theme == ThemeMode.Dark;
        if (settings.Theme == ThemeMode.System)
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        var background = dark ? Color.FromArgb(26, 29, 34) : Color.FromArgb(246, 248, 250);
        var surface = dark ? Color.FromArgb(36, 40, 46) : Color.White;
        var foreground = dark ? Color.FromArgb(230, 233, 238) : Color.FromArgb(28, 38, 48);
        var border = dark ? Color.FromArgb(62, 69, 78) : Color.FromArgb(222, 228, 234);
        var accent = Color.FromArgb(0, 109, 177);
        if (SystemInformation.HighContrast)
        {
            background = SystemColors.Control;
            surface = SystemColors.Window;
            foreground = SystemColors.WindowText;
            accent = SystemColors.Highlight;
            border = SystemColors.WindowText;
        }
        Apply(target ?? this);
        copy.BackColor = accent;
        copy.ForeColor = Color.White;
        if (IsHandleCreated)
        {
            int enabled = dark ? 1 : 0;
            DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int));
        }

        void Apply(Control control)
        {
            control.BackColor = control is TextBox or ComboBox or DataGridView ? surface : background;
            control.ForeColor = foreground;
            if (control is Button button) button.FlatAppearance.BorderColor = border;
            if (control is DataGridView view)
            {
                view.BackgroundColor = surface;
                view.GridColor = border;
                view.DefaultCellStyle.BackColor = surface;
                view.DefaultCellStyle.ForeColor = foreground;
                view.DefaultCellStyle.SelectionBackColor = dark ? Color.FromArgb(29, 67, 94) : Color.FromArgb(219, 238, 250);
                view.DefaultCellStyle.SelectionForeColor = foreground;
                view.ColumnHeadersDefaultCellStyle.BackColor = background;
                view.ColumnHeadersDefaultCellStyle.ForeColor = foreground;
            }
            foreach (Control child in control.Controls) Apply(child);
        }
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTheme();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged;
            timer.Dispose();
            thumbnailGeneration++;
            thumbnail.Image?.Dispose();
            cancellation?.Dispose();
        }
        base.Dispose(disposing);
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    internal void RenderForTest(string path, string locale, ThemeMode mode, ScanOptions options, List<PlanItem> items)
    {
        settings.Language = locale;
        settings.Theme = mode;
        text.Load(locale);
        ApplyLanguage();
        ApplyTheme();
        source.Text = options.Source;
        destination.Text = options.Destination;
        plan = items;
        previewOptions = options;
        RefreshRows();
        ShowInTaskbar = false;
        Opacity = 0;
        Show();
        Application.DoEvents();
        PerformLayout();
        foreach (var control in new Control[] { language, theme, source, destination, scan, copy, cancel })
        {
            if (!control.Visible || !control.Parent!.ClientRectangle.Contains(control.Bounds))
                throw new InvalidOperationException("Clipped UI control: " + control.AccessibleName);
        }
        using var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
        bitmap.Save(path);
    }
}
