using System.Diagnostics;
using PhotoOrganizer;

internal static class UiWorkflowTests
{
    private sealed class TestDialogs : IWindowDialogs
    {
        public int Confirmations { get; private set; }
        public List<string> Notifications { get; } = [];
        public bool Confirm(IWin32Window owner, string message, string title) { Confirmations++; return true; }
        public void Notify(IWin32Window owner, string message, string title) => Notifications.Add(message);
    }

    public static void Run(string root)
    {
        string fixtureSource = Path.Combine(root, "ui-flow-source");
        string archive = Path.Combine(root, "ui-flow-archive");
        string data = Path.Combine(root, "ui-flow-data");
        Directory.CreateDirectory(fixtureSource);
        Directory.CreateDirectory(archive);
        foreach (var file in Directory.GetFiles(Path.Combine(root, "source"))) File.Copy(file, Path.Combine(fixtureSource, Path.GetFileName(file)));
        var dialogs = new TestDialogs();
        using var form = new MainForm(data, dialogs) { ShowInTaskbar = false, Opacity = 0 };
        Exception? failure = null;
        form.Shown += async (_, _) =>
        {
            try
            {
                for (int locale = 0; locale < 2; locale++)
                {
                    Find<ComboBox>(form, "LanguageChoice").SelectedIndex = locale;
                    for (int theme = 0; theme < 3; theme++)
                    {
                        Find<ComboBox>(form, "ThemeChoice").SelectedIndex = theme;
                        var saved = AppSettings.Load(data);
                        Check(saved.Language == (locale == 0 ? "zh-CN" : "en") && saved.Theme == (ThemeMode)theme,
                            "UI language/theme selection persisted: " + locale + "/" + theme);
                    }
                }
                Find<TextBox>(form, "SourcePath").Text = fixtureSource;
                Find<TextBox>(form, "DestinationPath").Text = archive;
                form.Size = form.MinimumSize;
                await Task.Delay(50);
                Check(Find<DataGridView>(form, "PreviewGrid").Height >= 80, "Preview remains usable at minimum window size");
                form.Size = new Size(1400, 940);
                Find<Button>(form, "Preview").PerformClick();
                await WaitForTaskAsync(form);
                Check(Find<DataGridView>(form, "PreviewGrid").RowCount == 4, "Scan button populated the actual preview grid");
                Find<Button>(form, "Copy").PerformClick();
                await WaitForTaskAsync(form);
                using (var journal = new Journal(data)) Check(journal.Read().Count(x => x.State == "Committed") == 2,
                    "Copy button completed the confirmed preview");
                Find<Button>(form, "History").PerformClick();
                var history = Find<DataGridView>(form, "HistoryGrid");
                Check(history.Visible && history.RowCount == 2, "History button displays durable operations");
                Capture(form, Path.Combine(root, "ui-history.png"));
                history.SelectAll();
                Find<Button>(form, "UndoPreview").PerformClick();
                await WaitForTaskAsync(form);
                Check(history.RowCount == 2, "Undo button presents a full destination preview");
                Check(Find<Button>(form, "UndoPreview").AccessibleName == Find<Button>(form, "UndoPreview").Text,
                    "Undo action accessibility name matches its execution stage");
                Capture(form, Path.Combine(root, "ui-undo-preview.png"));
                Find<Button>(form, "UndoPreview").PerformClick();
                await WaitForTaskAsync(form);
                using (var journal = new Journal(data)) Check(journal.Read().All(x => x.State == "Undone"), "UI copy undo completed");

                Find<Button>(form, "History").PerformClick();
                Find<ComboBox>(form, "OperationChoice").SelectedIndex = 1;
                Check(!Find<TextBox>(form, "DestinationPath").Enabled, "Rename mode disables the archive destination");
                Check(Find<Button>(form, "Copy").AccessibleName == Find<Button>(form, "Copy").Text,
                    "Rename button accessibility name matches the operation");
                Find<Button>(form, "Preview").PerformClick();
                await WaitForTaskAsync(form);
                Find<Button>(form, "Copy").PerformClick();
                await WaitForTaskAsync(form);
                using (var journal = new Journal(data)) Check(journal.Read().Count(x => x.Kind == "Rename" && x.State == "Committed") == 2,
                    "UI rename flow uses the confirmed preview");
                Find<Button>(form, "History").PerformClick();
                history.ClearSelection();
                foreach (DataGridViewRow row in history.Rows) row.Selected = row.Cells[3].Value?.ToString() == "Completed";
                Find<Button>(form, "UndoPreview").PerformClick();
                await WaitForTaskAsync(form);
                Find<Button>(form, "UndoPreview").PerformClick();
                await WaitForTaskAsync(form);
                Check(File.Exists(Path.Combine(fixtureSource, "a.png")) && File.Exists(Path.Combine(fixtureSource, "b.PNG")),
                    "UI rename undo restores original names");
                Check(dialogs.Confirmations == 4 && dialogs.Notifications.Count == 0, "All four destructive or writing actions requested confirmation without hidden errors");
            }
            catch (Exception ex) { failure = ex; }
            finally { form.Close(); }
        };
        Application.Run(form);
        if (failure is not null) throw new Exception("UI workflow failed", failure);
    }
    private static T Find<T>(Control form, string name) where T : Control =>
        form.Controls.Find(name, true).OfType<T>().Single();
    private static void Capture(Form form, string path)
    {
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, form.Bounds with { X = 0, Y = 0 });
        bitmap.Save(path);
    }
    private static async Task WaitForTaskAsync(Control form)
    {
        var clock = Stopwatch.StartNew();
        while (Find<Button>(form, "Cancel").Enabled)
        {
            if (clock.Elapsed.TotalSeconds > 30) throw new TimeoutException("UI operation did not finish");
            await Task.Delay(20);
        }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
