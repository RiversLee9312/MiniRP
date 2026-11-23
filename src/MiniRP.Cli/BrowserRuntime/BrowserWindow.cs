using System.Runtime.InteropServices;

namespace MiniRP.Cli;

internal static partial class BrowserWindow
{
    public static void DetachConsole()
    {
        if (!Console.IsOutputRedirected) FreeConsole();
    }

    public static async Task ShowError(string detail)
    {
        if (OperatingSystem.IsWindows()) MessageBox(IntPtr.Zero, detail, "MiniRP", 0x10u);
        else if (BrowserSelection.HasDisplay && BrowserSelection.FindOnPath("zenity") is { } zenity)
        {
            await using var dialog = BrowserProcess.Start(zenity, "--error", "--no-markup", "--title=MiniRP", "--text=" + detail);
            await dialog.Exit(CancellationToken.None);
        }
        else Console.Error.WriteLine(detail);
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr window, string text, string caption, uint type);
}
