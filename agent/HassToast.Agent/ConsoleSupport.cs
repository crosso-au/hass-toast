using System.Runtime.InteropServices;

namespace HassToast.Agent;

/// <summary>
/// Console handling for an app that is both a command-line tool and a background tray agent.
/// <para>
/// The executable targets the console subsystem so that cmd and PowerShell wait for it and its
/// output reaches the caller normally. A GUI-subsystem build did neither: the shell returned to
/// its prompt before the process wrote anything, and output only arrived if AttachConsole
/// happened to find the caller's console.
/// </para>
/// <para>
/// The cost is that tray mode inherits a console window, which <see cref="HideWindow"/> removes.
/// </para>
/// </summary>
internal static class ConsoleSupport
{
    private const int SwHide = 0;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    /// <summary>
    /// Hides the console window for background tray operation.
    /// <para>
    /// Only the window is hidden, not freed — the process keeps its standard handles, so logging
    /// and any diagnostic writes stay valid rather than failing against a closed handle.
    /// </para>
    /// </summary>
    public static void HideWindow()
    {
        var window = GetConsoleWindow();
        if (window != IntPtr.Zero) ShowWindow(window, SwHide);
    }

    /// <summary>Reads a line without echoing it, for tokens and other secrets.</summary>
    public static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        var buffer = new System.Text.StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return buffer.ToString();
                case ConsoleKey.Backspace when buffer.Length > 0:
                    buffer.Length--;
                    Console.Write("\b \b");
                    break;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        buffer.Append(key.KeyChar);
                        Console.Write('*');
                    }
                    break;
            }
        }
    }

    public static string Prompt(string label, string? @default = null)
    {
        Console.Write(@default is null ? $"{label}: " : $"{label} [{@default}]: ");
        var value = Console.ReadLine();
        return string.IsNullOrWhiteSpace(value) ? (@default ?? "") : value.Trim();
    }
}
