using System.Runtime.InteropServices;

namespace HassToast.Agent.Toasts;

/// <summary>
/// Creates the Start Menu shortcut that makes this app real to the Windows shell.
/// <para>
/// An unpackaged desktop app cannot display toasts without one. The shell resolves a
/// notification's owning app through a Start Menu shortcut carrying the AppUserModelID; with no
/// such shortcut there is no app record, so Windows accepts every notification through the API,
/// assigns it an id, lists it via GetAllAsync — and never draws it. No banner, no Action Center
/// entry, no error anywhere. That failure is indistinguishable from success unless you are
/// looking at the screen.
/// </para>
/// </summary>
public static class StartMenuShortcut
{
    /// <summary>Why the last <see cref="Ensure"/> call failed, if it did.</summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// CLSID of the COM activator to record on the shortcut. Without it Windows has nowhere to
    /// deliver a click, and buttons render but do nothing.
    /// </summary>
    public static Guid? ToastActivatorClsid { get; set; }

    /// <summary>Ensures the shortcut exists, returning true if it is present afterwards.</summary>
    /// <param name="targetPath">
    /// What the shortcut should point at. Defaults to the running executable, which is right for
    /// the agent registering itself and wrong for an installer registering on its behalf.
    /// </param>
    public static bool Ensure(
        string aumid, string displayName, string? iconPath, out string path, string? targetPath = null)
    {
        LastError = null;

        path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs", $"{displayName}.lnk");

        try
        {
            var target = targetPath ?? Environment.ProcessPath;
            if (string.IsNullOrEmpty(target))
            {
                LastError = "Environment.ProcessPath is empty.";
                return false;
            }

            if (File.Exists(path) && ShortcutMatches(path, target, aumid, iconPath)) return true;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Create(path, target, aumid, displayName, iconPath);

            if (File.Exists(path)) return true;

            LastError = "The shortcut was written but is not on disk.";
            return false;
        }
        catch (Exception ex)
        {
            // Swallowing this was a mistake the first time round: a silent failure here looks
            // exactly like a working app whose toasts never appear.
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    public static void Delete(string displayName)
    {
        var path = Path.Combine(ProgramsFolder, $"{displayName}.lnk");
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>
    /// Removes shortcuts carrying our AUMID under any name other than the current one.
    /// <para>
    /// Windows takes the app's display name from the shortcut's file name, so renaming means
    /// writing a new shortcut. Left alone, the old one keeps claiming the same AUMID and the
    /// app appears twice in notification settings, with Windows free to pick either name.
    /// </para>
    /// </summary>
    public static int RemoveOtherShortcutsFor(string aumid, string keepDisplayName)
        => RemoveShortcuts(aumid, Path.Combine(ProgramsFolder, $"{keepDisplayName}.lnk"));

    /// <summary>
    /// Removes every shortcut carrying our AUMID, whatever it happens to be called.
    /// <para>
    /// Uninstall cannot go by name. The display name is configurable, so the shortcut may be
    /// called anything, and by the time uninstall runs the config that recorded the name has
    /// usually already gone. The AUMID is the part that is genuinely fixed, and it is what
    /// Windows keys the app's notification record on — so it is what has to be matched.
    /// </para>
    /// </summary>
    public static int RemoveAllShortcutsFor(string aumid) => RemoveShortcuts(aumid, keep: null);

    /// <summary>
    /// Every shortcut in the Start Menu carrying <paramref name="aumid"/>, whatever it is named.
    /// <para>
    /// Exposed separately from removal so the uninstall audit can report what is still there
    /// without having to try deleting it to find out.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> FindShortcutsFor(string aumid)
    {
        var found = new List<string>();

        try
        {
            if (!Directory.Exists(ProgramsFolder)) return found;

            foreach (var file in Directory.EnumerateFiles(ProgramsFolder, "*.lnk"))
            {
                if (Read(file)?.Aumid == aumid) found.Add(file);
            }
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
        }

        return found;
    }

    private static int RemoveShortcuts(string aumid, string? keep)
    {
        var removed = 0;

        try
        {
            foreach (var file in FindShortcutsFor(aumid))
            {
                if (keep is not null && string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
                    continue;

                File.Delete(file);
                removed++;
            }
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
        }

        return removed;
    }

    private static string ProgramsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs");

    /// <summary>What a shortcut on disk actually says, for diagnostics.</summary>
    public sealed record ShortcutDetails(string Target, string? Aumid, Guid? ActivatorClsid);

    /// <summary>
    /// Reads a shortcut back. Everything that decides whether toasts work is recorded inside the
    /// .lnk rather than anywhere inspectable, so without this a broken shortcut is invisible.
    /// </summary>
    public static ShortcutDetails? Read(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(path, 0);

            var buffer = new System.Text.StringBuilder(260);
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);

            var store = (IPropertyStore)link;

            return new ShortcutDetails(
                buffer.ToString(),
                ReadStringProperty(store, PropertyKeys.AppUserModelId),
                ReadClsidProperty(store, PropertyKeys.ToastActivatorClsid));
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    private static string? ReadStringProperty(IPropertyStore store, PropertyKey key)
    {
        store.GetValue(ref key, out var value);
        try
        {
            return value.VarType == VtLpwstr && value.Pointer != IntPtr.Zero
                ? Marshal.PtrToStringUni(value.Pointer)
                : null;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    private static Guid? ReadClsidProperty(IPropertyStore store, PropertyKey key)
    {
        store.GetValue(ref key, out var value);
        try
        {
            return value.VarType == VtClsid && value.Pointer != IntPtr.Zero
                ? Marshal.PtrToStructure<Guid>(value.Pointer)
                : null;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    /// <summary>
    /// Reads back the target, AUMID and icon so a stale shortcut is replaced, not trusted. The
    /// icon matters as much as the rest: earlier builds pointed it at a PNG, which the toast
    /// header draws as a blank page, and nothing else about those shortcuts is wrong.
    /// </summary>
    private static bool ShortcutMatches(
        string path, string expectedTarget, string expectedAumid, string? expectedIcon)
    {
        var link = (IShellLinkW)new ShellLink();
        ((IPersistFile)link).Load(path, 0);

        var buffer = new System.Text.StringBuilder(260);
        link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
        var target = buffer.ToString();

        if (!string.IsNullOrEmpty(expectedIcon) && File.Exists(expectedIcon))
        {
            var icon = new System.Text.StringBuilder(260);
            link.GetIconLocation(icon, icon.Capacity, out _);

            if (!string.Equals(icon.ToString(), expectedIcon, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        var store = (IPropertyStore)link;
        var key = PropertyKeys.AppUserModelId;
        store.GetValue(ref key, out var value);

        string? aumid = null;
        try
        {
            if (value.VarType == VtLpwstr && value.Pointer != IntPtr.Zero)
                aumid = Marshal.PtrToStringUni(value.Pointer);
        }
        finally
        {
            PropVariantClear(ref value);
        }

        return string.Equals(target, expectedTarget, StringComparison.OrdinalIgnoreCase)
               && string.Equals(aumid, expectedAumid, StringComparison.Ordinal);
    }

    private static void Create(string path, string target, string aumid, string displayName, string? iconPath)
    {
        var link = (IShellLinkW)new ShellLink();

        link.SetPath(target);
        link.SetArguments("");
        link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? "");
        link.SetDescription(displayName);

        if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
            link.SetIconLocation(iconPath, 0);

        // The AUMID is the part that matters: it is what ties a notification back to this app.
        var store = (IPropertyStore)link;
        var key = PropertyKeys.AppUserModelId;

        // Built by hand rather than via InitPropVariantFromString, which is an inline helper in
        // propvarutil.h and not exported from any DLL. PropVariantClear frees the string, so it
        // has to be allocated with the COM task allocator.
        var value = new PropVariant
        {
            VarType = VtLpwstr,
            Pointer = Marshal.StringToCoTaskMemUni(aumid),
        };

        try
        {
            store.SetValue(ref key, ref value);
        }
        finally
        {
            PropVariantClear(ref value);
        }

        // The activator CLSID is what lets a click reach the app at all.
        if (ToastActivatorClsid is { } clsid)
        {
            var activatorKey = PropertyKeys.ToastActivatorClsid;
            var guidMemory = Marshal.AllocCoTaskMem(Marshal.SizeOf<Guid>());
            Marshal.StructureToPtr(clsid, guidMemory, false);

            var activatorValue = new PropVariant { VarType = VtClsid, Pointer = guidMemory };
            try
            {
                store.SetValue(ref activatorKey, ref activatorValue);
            }
            finally
            {
                PropVariantClear(ref activatorValue);
            }
        }

        store.Commit();
        ((IPersistFile)link).Save(path, true);
    }

    private const ushort VtLpwstr = 31;
    private const ushort VtClsid = 72;

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int PropVariantClear(ref PropVariant variant);

    private static class PropertyKeys
    {
        /// <summary>System.AppUserModel.ID</summary>
        public static PropertyKey AppUserModelId => new(
            new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

        /// <summary>System.AppUserModel.ToastActivatorCLSID</summary>
        public static PropertyKey ToastActivatorClsid => new(
            new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 26);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort VarType;
        private readonly ushort _reserved1;
        private readonly ushort _reserved2;
        private readonly ushort _reserved3;
        public IntPtr Pointer;
        private readonly IntPtr _reserved4;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport,
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        // StringBuilder, not char[]: LPWStr cannot marshal a char[] and throws at call time.
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int maxArgs);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder iconPath, int iconPathLength, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRel, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport,
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    [ComImport,
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }
}
