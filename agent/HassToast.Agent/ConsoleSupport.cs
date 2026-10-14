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
/// The cost is that tray mode can be handed a console window, which <see cref="ReleaseConsole"/>
/// gets rid of.
/// </para>
/// </summary>
internal static class ConsoleSupport
{
    private const int SwHide = 0;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList(uint[] processList, uint count);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    /// <summary>
    /// Gets the console out of the way for background tray operation.
    /// <para>
    /// A console created just for this process — a sign-in launch from the Run key, a toast
    /// click — is detached from outright. Hiding it is not enough: with Windows Terminal as the
    /// default terminal, the console window is a stand-in and hiding it leaves the Terminal tab
    /// on screen. Standard output is pointed at nothing first, so a stray write cannot fail
    /// against the closed handle. On Windows 11 24H2 and later the manifest's console policy
    /// means there is usually no console here at all.
    /// </para>
    /// <para>
    /// A console shared with a terminal the user ran the agent from is only hidden: that
    /// terminal is theirs, and detaching would leave it waiting on a process it no longer sees.
    /// </para>
    /// </summary>
    public static void ReleaseConsole()
    {
        var window = GetConsoleWindow();
        if (window == IntPtr.Zero) return;

        if (GetConsoleProcessList(new uint[2], 2) <= 1)
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            FreeConsole();
            return;
        }

        ShowWindow(window, SwHide);
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
