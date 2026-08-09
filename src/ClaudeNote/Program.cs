using System.IO;
using System.Windows.Forms;

namespace ClaudeNote;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var config = AppConfig.LoadDefault();

        if (args.Length > 0)
            return DebugCommands.Run(args, config);

        using var mutex = new Mutex(initiallyOwned: true, "ClaudeNote-SingleInstance", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("ClaudeNote は既に起動しています。タスクトレイを確認してください。",
                "ClaudeNote", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext(config));
        return 0;
    }
}
