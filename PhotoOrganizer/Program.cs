namespace PhotoOrganizer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--diagnostics"] or ["--diagnostics-file", _])
        {
            string result = Diagnostics.Create(AppSettings.Load(AppSettings.DataDirectory));
            if (args.Length == 2) File.WriteAllText(args[1], result);
            else Console.WriteLine(result);
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
