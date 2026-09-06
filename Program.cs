using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PS2-HDD-File-Manager.Tests")]

namespace PfsExplorerLite;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
