using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace SerPlayer;

/// <summary>Who an installation is for: just the current account (HKCU, no admin) or everyone (HKLM, Program Files).</summary>
enum InstallScope { CurrentUser, AllUsers }

/// <summary>What the user chose in the setup wizard.</summary>
sealed record InstallOptions(InstallScope Scope, string Directory, bool StartMenuShortcut, bool DesktopShortcut);

/// <summary>
/// Install engine. The exe is self-contained (no .NET download needed) and copies itself into the chosen folder.
/// Per-user installs register under HKCU and need no admin; all-users installs register under HKLM and must run elevated
/// (the wizard does that work in an elevated helper process, see <see cref="ElevatedHelper"/>).
/// </summary>
static class Installer
{
    public const string AppName = AppInfo.Name;
    const string ExeName = "SerPlayer.exe";
    const string ProgId = "SerPlayer.ser";
    const string AutoProgId = "ser_auto_file";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SerPlayer";
    const string CapabilitiesKey = @"Software\SerPlayer\Capabilities";

    public static string Version => Application.ProductVersion.Split('+')[0];

    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    static RegistryKey Root(InstallScope s) => s == InstallScope.AllUsers ? Registry.LocalMachine : Registry.CurrentUser;

    public static string DefaultDirectory(InstallScope s) => s == InstallScope.AllUsers
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SerPlayer");

    static string StartMenuShortcut(InstallScope s) => Path.Combine(Environment.GetFolderPath(
        s == InstallScope.AllUsers ? Environment.SpecialFolder.CommonPrograms : Environment.SpecialFolder.Programs), AppName + ".lnk");

    static string DesktopShortcut(InstallScope s) => Path.Combine(Environment.GetFolderPath(
        s == InstallScope.AllUsers ? Environment.SpecialFolder.CommonDesktopDirectory : Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    /// <summary>Shared, user-writable crash-log folder for all-users installs (Program Files isn't writable by the app).</summary>
    public static string MachineLogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppName, "CrashLogs");

    /// <summary>Folder of the installation for <paramref name="s"/>, or null if not installed that way.</summary>
    public static string? InstalledDirectory(InstallScope s)
    {
        using var k = Root(s).OpenSubKey(UninstallKey);
        return k?.GetValue("InstallLocation") is string dir && File.Exists(Path.Combine(dir, ExeName)) ? dir : null;
    }

    public static string? InstalledVersion(InstallScope s)
    {
        if (InstalledDirectory(s) == null) return null;
        using var k = Root(s).OpenSubKey(UninstallKey);
        return k?.GetValue("DisplayVersion") as string;
    }

    public static IEnumerable<InstallScope> InstalledScopes() =>
        new[] { InstallScope.CurrentUser, InstallScope.AllUsers }.Where(s => InstalledDirectory(s) != null);

    /// <summary>The installed exe Windows should point at (per-user copy first, as HKCU wins over HKLM).</summary>
    internal static string InstalledExe => Path.Combine(
        InstalledDirectory(InstallScope.CurrentUser) ?? InstalledDirectory(InstallScope.AllUsers) ?? DefaultDirectory(InstallScope.CurrentUser), ExeName);

    public static bool HasDesktopShortcut(InstallScope s) => File.Exists(DesktopShortcut(s));
    public static bool HasStartMenuShortcut(InstallScope s) => File.Exists(StartMenuShortcut(s));

    /// <summary>True if SER Player is currently running from <paramref name="directory"/> (other than this process).</summary>
    public static bool IsRunningFrom(string directory)
    {
        string target = Path.GetFullPath(Path.Combine(directory, ExeName));
        foreach (var p in Process.GetProcessesByName("SerPlayer"))
        {
            try
            {
                if (p.Id != Environment.ProcessId &&
                    string.Equals(p.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { /* access denied etc. */ }
            finally { p.Dispose(); }
        }
        return false;
    }

    /// <summary>Validates the chosen folder. Per-user installs must be writable without admin; all-users ones are written elevated.</summary>
    public static string? CheckDirectory(string dir, InstallScope scope)
    {
        try
        {
            dir = Path.GetFullPath(dir);
            if (Path.GetPathRoot(dir) == dir) return "Choose a folder, not the root of a drive.";
            if (scope == InstallScope.AllUsers && !IsElevated) return null; // the elevated helper creates it
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return "You don't have permission to install there. Choose a folder in your user profile, " +
                   "or go back and choose \"Install for all users\".";
        }
        catch (Exception ex) { return ex.Message; }
    }

    // ───────────────────────── install ─────────────────────────

    public static void Install(InstallOptions o, Action<string, double>? progress = null)
    {
        void Step(string text, double fraction) => progress?.Invoke(text, fraction);
        var scope = o.Scope;
        if (scope == InstallScope.AllUsers && !IsElevated)
            throw new UnauthorizedAccessException("Installing for all users requires administrator permission.");
        string dir = Path.GetFullPath(o.Directory);
        string exe = Path.Combine(dir, ExeName);
        string? previousDir = InstalledDirectory(scope);

        Step("Copying files…", 0.05);
        Directory.CreateDirectory(dir);
        string src = Environment.ProcessPath!;
        if (!string.Equals(Path.GetFullPath(src), exe, StringComparison.OrdinalIgnoreCase))
            CopyWithProgress(src, exe, f => Step("Copying files…", 0.05 + 0.55 * f));
        string fileIcon = $"\"{WriteFileIcon(dir)}\",0";
        WriteResource("SerPlayer.LICENSE.txt", Path.Combine(dir, AppInfo.LicenseFile));
        WriteResource("SerPlayer.THIRD-PARTY-NOTICES.txt", Path.Combine(dir, AppInfo.NoticesFile));
        if (scope == InstallScope.AllUsers) PrepareMachineLogDirectory();

        Step("Registering the .ser file type…", 0.65);
        RegisterFileType(scope, exe, fileIcon);

        Step("Adding to Settings → Apps…", 0.75);
        string scopeArg = scope == InstallScope.AllUsers ? " --allusers" : "";
        using (var k = Root(scope).CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", AppName);
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", AppInfo.Publisher);
            k.SetValue("URLInfoAbout", AppInfo.Website);
            k.SetValue("HelpLink", AppInfo.Repository);
            k.SetValue("Comments", $"{AppInfo.Tagline}. Licensed under {AppInfo.License}.");
            k.SetValue("DisplayIcon", $"\"{exe}\",0");
            k.SetValue("InstallLocation", dir);
            k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            k.SetValue("UninstallString", $"\"{exe}\" --uninstall{scopeArg}");
            k.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall --quiet{scopeArg}");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
        }

        Step("Creating shortcuts…", 0.85);
        if (o.StartMenuShortcut) CreateShortcut(StartMenuShortcut(scope), exe); else DeleteIfOurs(StartMenuShortcut(scope));
        if (o.DesktopShortcut) CreateShortcut(DesktopShortcut(scope), exe); else DeleteIfOurs(DesktopShortcut(scope));

        // Moved to a new folder: remove the old copy's files.
        if (previousDir != null && !string.Equals(Path.GetFullPath(previousDir), dir, StringComparison.OrdinalIgnoreCase))
            try { RemoveOurFiles(previousDir); } catch { }

        Step("Refreshing icons…", 0.93);
        NotifyShell();
        RefreshIconCache();
        Step("Done", 1.0);
    }

    static void CopyWithProgress(string src, string dst, Action<double> progress)
    {
        string tmp = dst + ".new";
        using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        {
            var buf = new byte[1 << 20];
            long total = input.Length, done = 0;
            int n;
            while ((n = input.Read(buf, 0, buf.Length)) > 0)
            {
                output.Write(buf, 0, n);
                done += n;
                progress(total > 0 ? (double)done / total : 1);
            }
        }
        File.Move(tmp, dst, overwrite: true);
    }

    /// <summary>ProgramData\SER Player\CrashLogs, writable by every user (the app runs unelevated).</summary>
    static void PrepareMachineLogDirectory()
    {
        var di = Directory.CreateDirectory(MachineLogDirectory);
        var acl = di.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        di.SetAccessControl(acl);
    }

    static void RegisterFileType(InstallScope scope, string exe, string fileIcon)
    {
        string open = $"\"{exe}\" \"%1\"";
        var root = Root(scope);
        using (var classes = root.CreateSubKey(@"Software\Classes"))
        {
            // ProgID
            using (var k = classes.CreateSubKey(ProgId))
            {
                k.SetValue("", "SER Video");
                k.SetValue("FriendlyTypeName", "SER Video");
                using (var ic = k.CreateSubKey("DefaultIcon")) ic.SetValue("", fileIcon);
                using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", open);
            }
            // Show in "Open with" for .ser
            using (var k = classes.CreateSubKey(@".ser\OpenWithProgids")) k.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            // Explorer's "Type" column comes from the extension's default ProgID. Claim it unless another app owns it
            // (Windows' own ser_auto_file from "Browse…" doesn't count; it has no name, hence "SER File").
            using (var ext = classes.CreateSubKey(".ser"))
            {
                if (ext.GetValue("") is not string cur || cur.Length == 0 || cur == AutoProgId || cur == ProgId)
                {
                    ext.SetValue("", ProgId);
                    ext.SetValue("Content Type", "video/x-ser");
                }
            }
            // Application registration (Open with → Choose another app). When the user picks SER Player as the
            // default ("Always"), Windows' UserChoice points at this key, so it needs its own name and icon too.
            using (var k = classes.CreateSubKey(@"Applications\" + ExeName))
            {
                k.SetValue("FriendlyAppName", AppName);
                k.SetValue("", "SER Video");
                k.SetValue("FriendlyTypeName", "SER Video");
                using (var ic = k.CreateSubKey("DefaultIcon")) ic.SetValue("", fileIcon);
                using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", open);
                using (var st = k.CreateSubKey("SupportedTypes")) st.SetValue(".ser", "");
            }
            // "Open with → Browse…" makes Windows create a per-user ser_auto_file pointing at whichever SerPlayer.exe was
            // picked. If it refers to SER Player, point it at this copy and give it the name and icon as well.
            if (scope == InstallScope.CurrentUser)
                using (var cmd = classes.OpenSubKey(AutoProgId + @"\shell\open\command", writable: true))
                {
                    if (cmd?.GetValue("") is string v && v.Contains(ExeName, StringComparison.OrdinalIgnoreCase))
                    {
                        cmd.SetValue("", open);
                        using var ic = classes.CreateSubKey(AutoProgId + @"\DefaultIcon");
                        ic.SetValue("", fileIcon);
                        using var auto = classes.CreateSubKey(AutoProgId);
                        auto.SetValue("", "SER Video");
                        auto.SetValue("FriendlyTypeName", "SER Video");
                    }
                }
        }

        // Register with Settings → Default apps, so SER Player is listed there by name.
        using (var cap = root.CreateSubKey(CapabilitiesKey))
        {
            cap.SetValue("ApplicationName", AppName);
            cap.SetValue("ApplicationDescription", AppInfo.Tagline);
            cap.SetValue("ApplicationIcon", fileIcon);
            using (var fa = cap.CreateSubKey("FileAssociations")) fa.SetValue(".ser", ProgId);
        }
        using (var ra = root.CreateSubKey(@"Software\RegisteredApplications"))
            ra.SetValue(AppName, CapabilitiesKey);
    }

    /// <summary>
    /// Writes the app icon as SerPlayer-&lt;hash&gt;.ico in the install folder and removes older ones. Explorer caches icons
    /// by path, so a content-derived name guarantees .ser files pick up a changed icon instead of a stale cached one.
    /// </summary>
    static string WriteFileIcon(string dir)
    {
        using var res = typeof(Installer).Assembly.GetManifestResourceStream("SerPlayer.app.ico")
                        ?? throw new InvalidOperationException("Embedded icon missing.");
        using var ms = new MemoryStream();
        res.CopyTo(ms);
        byte[] bytes = ms.ToArray();
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..8].ToLowerInvariant();
        string path = Path.Combine(dir, $"SerPlayer-{hash}.ico");
        if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
        foreach (var old in Directory.GetFiles(dir, "SerPlayer-*.ico"))
            if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(old); } catch { }
        return path;
    }

    /// <summary>Writes an embedded text resource (license, notices) into the install folder.</summary>
    static void WriteResource(string name, string path)
    {
        using var res = typeof(Installer).Assembly.GetManifestResourceStream(name)
                        ?? throw new InvalidOperationException($"Embedded {name} missing.");
        using var file = File.Create(path);
        res.CopyTo(file);
    }

    // ───────────────────────── uninstall ─────────────────────────

    /// <summary>
    /// Removes the installation for <paramref name="scope"/>. With <paramref name="forgetDefault"/> (a real uninstall, not
    /// a move between scopes) the current user's Windows records are cleared too, so SER Player stops being the default.
    /// </summary>
    public static void Uninstall(InstallScope scope, bool forgetDefault, Action<string, double>? progress = null)
    {
        void Step(string text, double fraction) => progress?.Invoke(text, fraction);
        if (scope == InstallScope.AllUsers && !IsElevated)
            throw new UnauthorizedAccessException("Removing an all-users installation requires administrator permission.");
        string? dir = InstalledDirectory(scope);
        var root = Root(scope);

        Step("Removing the .ser file type…", 0.2);
        bool autoIsOurs = false;
        using (var classes = root.OpenSubKey(@"Software\Classes", writable: true))
        {
            classes?.DeleteSubKeyTree(ProgId, false);
            classes?.DeleteSubKeyTree(@"Applications\" + ExeName, false);
            if (scope == InstallScope.CurrentUser)
            {
                // Remove Windows' "Browse…" association too, if it points at SER Player (it would point at a deleted exe).
                using (var cmd = classes?.OpenSubKey(AutoProgId + @"\shell\open\command"))
                    autoIsOurs = cmd?.GetValue("") is string v && v.Contains(ExeName, StringComparison.OrdinalIgnoreCase);
                if (autoIsOurs && forgetDefault) classes?.DeleteSubKeyTree(AutoProgId, false);
            }
        }
        // Our values under <root>\Software\Classes\.ser (always), plus the user's own records when really uninstalling.
        ForgetClassesEntries(root, ".ser", autoIsOurs && forgetDefault);
        if (forgetDefault && scope == InstallScope.CurrentUser) ForgetUserRecords(".ser", autoIsOurs);

        Step("Removing from Settings → Apps…", 0.45);
        root.DeleteSubKeyTree(UninstallKey, false);
        root.DeleteSubKeyTree(@"Software\SerPlayer", false);
        using (var ra = root.OpenSubKey(@"Software\RegisteredApplications", writable: true))
            ra?.DeleteValue(AppName, false);

        Step("Removing shortcuts…", 0.65);
        DeleteIfOurs(StartMenuShortcut(scope));
        DeleteIfOurs(DesktopShortcut(scope));
        NotifyShell();

        Step("Removing files…", 0.85);
        if (dir != null) RemoveOurFiles(dir);
        if (scope == InstallScope.AllUsers)
            try { Directory.Delete(Path.GetDirectoryName(MachineLogDirectory)!, recursive: true); } catch { }
        Step("Done", 1.0);
    }

    /// <summary>
    /// The current user's Windows records for a removed all-users install (the elevated helper may run as another account,
    /// so the wizard calls this itself afterwards). Skips nothing if SER Player is still installed for this user.
    /// </summary>
    public static void ForgetDefaultForCurrentUser()
    {
        if (InstalledDirectory(InstallScope.CurrentUser) != null) return;
        bool autoIsOurs;
        using (var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true))
        {
            using (var cmd = classes?.OpenSubKey(AutoProgId + @"\shell\open\command"))
                autoIsOurs = cmd?.GetValue("") is string v && v.Contains(ExeName, StringComparison.OrdinalIgnoreCase);
            if (autoIsOurs) classes?.DeleteSubKeyTree(AutoProgId, false);
        }
        ForgetClassesEntries(Registry.CurrentUser, ".ser", autoIsOurs);
        ForgetUserRecords(".ser", autoIsOurs);
    }

    /// <summary>
    /// After moving from a per-user to an all-users install: Windows' per-user "Browse…" association (ser_auto_file),
    /// if it points at SER Player, is repointed at the new copy so the user's default keeps working.
    /// </summary>
    public static void RetargetBrowseAssociation(string newDir)
    {
        string exe = Path.Combine(newDir, ExeName);
        using var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true);
        using var cmd = classes?.OpenSubKey(AutoProgId + @"\shell\open\command", writable: true);
        if (cmd?.GetValue("") is not string v || !v.Contains(ExeName, StringComparison.OrdinalIgnoreCase)) return;
        cmd.SetValue("", $"\"{exe}\" \"%1\"");
        if (Directory.Exists(newDir) && Directory.GetFiles(newDir, "SerPlayer-*.ico").FirstOrDefault() is string ico)
            using (var ic = classes!.CreateSubKey(AutoProgId + @"\DefaultIcon")) ic.SetValue("", $"\"{ico}\",0");
        NotifyShell();
    }

    static bool IsOurProgId(string? progId, bool autoProgIdIsOurs) => progId != null &&
        (progId == ProgId || progId.Equals(@"Applications\" + ExeName, StringComparison.OrdinalIgnoreCase) ||
         (autoProgIdIsOurs && progId == AutoProgId));

    /// <summary>Our default ProgID / Open-with entries under &lt;root&gt;\Software\Classes\&lt;ext&gt;, then the key if it's left empty.</summary>
    static void ForgetClassesEntries(RegistryKey root, string ext, bool autoProgIdIsOurs)
    {
        using var classes = root.OpenSubKey(@"Software\Classes", writable: true);
        if (classes == null) return;
        using (var k = classes.OpenSubKey(ext, writable: true))
        {
            if (k != null)
            {
                if (IsOurProgId(k.GetValue("") as string, autoProgIdIsOurs)) { k.DeleteValue("", false); k.DeleteValue("Content Type", false); }
                using (var owp = k.OpenSubKey("OpenWithProgids", writable: true))
                    foreach (var name in owp?.GetValueNames() ?? Array.Empty<string>())
                        if (IsOurProgId(name, autoProgIdIsOurs)) owp!.DeleteValue(name, false);
                DeleteIfEmpty(k, "OpenWithProgids");
            }
        }
        DeleteIfEmpty(classes, ext);
    }

    /// <summary>
    /// Clears what Windows remembers for this user about <paramref name="ext"/> that points at SER Player: the default-app
    /// choice (UserChoice / UserChoiceLatest), its place in the "opened with" history and Open-with lists, the "new app
    /// can open this" notification records, and the cached app name. Entries for other apps are left alone.
    /// </summary>
    internal static void ForgetUserRecords(string ext, bool autoProgIdWasOurs)
    {
        bool IsOurs(string? p) => IsOurProgId(p, autoProgIdWasOurs);

        using (var fileExts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts", writable: true))
        {
            using (var k = fileExts?.OpenSubKey(ext, writable: true))
            {
                if (k != null)
                {
                    // Default-app choice: UserChoice (ProgId value) on Windows 10, UserChoiceLatest (ProgId subkey) on newer 11.
                    foreach (var choice in new[] { "UserChoice", "UserChoiceLatest" })
                    {
                        bool ours;
                        using (var uc = k.OpenSubKey(choice))
                        using (var sub = uc?.OpenSubKey("ProgId"))
                            ours = IsOurs(uc?.GetValue("ProgId") as string) || IsOurs(sub?.GetValue("ProgId") as string);
                        if (ours)
                            try { k.DeleteSubKeyTree(choice, false); }
                            catch (Exception ex) { CrashLog.Write(ex, $"Clearing {ext} {choice}"); }
                    }
                    // "Opened with" history: single-letter values naming exes, ordered by MRUList.
                    using (var owl = k.OpenSubKey("OpenWithList", writable: true))
                    {
                        if (owl != null)
                        {
                            string mru = owl.GetValue("MRUList") as string ?? "";
                            foreach (var name in owl.GetValueNames())
                                if (name.Length == 1 && string.Equals(owl.GetValue(name) as string, ExeName, StringComparison.OrdinalIgnoreCase))
                                {
                                    owl.DeleteValue(name, false);
                                    mru = mru.Replace(name, "");
                                }
                            if (mru.Length > 0) owl.SetValue("MRUList", mru); else owl.DeleteValue("MRUList", false);
                        }
                    }
                    DeleteIfEmpty(k, "OpenWithList");
                    using (var owp = k.OpenSubKey("OpenWithProgids", writable: true))
                        foreach (var name in owp?.GetValueNames() ?? Array.Empty<string>())
                            if (IsOurs(name)) owp!.DeleteValue(name, false);
                    DeleteIfEmpty(k, "OpenWithProgids");
                }
            }
            if (fileExts != null) DeleteIfEmpty(fileExts, ext);
        }

        // "You have a new app that can open this type of file" records: "<progid>_<ext>".
        using (var toasts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\ApplicationAssociationToasts", writable: true))
            foreach (var name in toasts?.GetValueNames() ?? Array.Empty<string>())
                if (name.EndsWith("_" + ext, StringComparison.OrdinalIgnoreCase) && IsOurs(name[..^(ext.Length + 1)]))
                    toasts!.DeleteValue(name, false);

        // Windows' cached display name / company for any SerPlayer.exe (MuiCache).
        using (var mui = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache", writable: true))
            foreach (var name in mui?.GetValueNames() ?? Array.Empty<string>())
                if (name.Contains(@"\" + ExeName + ".", StringComparison.OrdinalIgnoreCase))
                    mui!.DeleteValue(name, false);
        NotifyShell();
    }

    /// <summary>Deletes parent\name if it has no values (other than an empty default) and no subkeys.</summary>
    static void DeleteIfEmpty(RegistryKey parent, string name)
    {
        bool empty;
        using (var k = parent.OpenSubKey(name))
        {
            if (k == null) return;
            empty = k.SubKeyCount == 0 &&
                    k.GetValueNames().All(v => v.Length == 0 && string.IsNullOrEmpty(k.GetValue(v) as string));
        }
        if (empty) try { parent.DeleteSubKeyTree(name, false); } catch { }
    }

    /// <summary>
    /// Deletes only what SER Player put in the folder (exe, file icon, license files, crash logs), then the folder if it's empty, so a
    /// custom location that holds other files is never wiped. If the exe is in use (this process, or the wizard that
    /// launched an elevated helper), a hidden cmd keeps retrying for up to two minutes until it's free.
    /// </summary>
    static void RemoveOurFiles(string dir)
    {
        string exe = Path.Combine(dir, ExeName);
        try
        {
            if (File.Exists(exe)) File.Delete(exe);
            foreach (var ico in Directory.GetFiles(dir, "SerPlayer-*.ico")) File.Delete(ico);
            foreach (var doc in new[] { AppInfo.LicenseFile, AppInfo.NoticesFile })
                if (File.Exists(Path.Combine(dir, doc))) File.Delete(Path.Combine(dir, doc));
            string logs = Path.Combine(dir, "CrashLogs");
            if (Directory.Exists(logs)) Directory.Delete(logs, recursive: true);
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            return;
        }
        catch (Exception) when (File.Exists(exe)) { /* exe in use: defer */ }

        string cmd = $"/c for /l %i in (1,1,120) do @(if exist \"{exe}\" (del /f /q \"{exe}\" >nul 2>&1 & if exist \"{exe}\" ping -n 2 127.0.0.1 >nul))" +
                     $" & del /f /q \"{Path.Combine(dir, "SerPlayer-*.ico")}\" >nul 2>&1" +
                     $" & del /f /q \"{Path.Combine(dir, AppInfo.LicenseFile)}\" \"{Path.Combine(dir, AppInfo.NoticesFile)}\" >nul 2>&1" +
                     $" & rmdir /s /q \"{Path.Combine(dir, "CrashLogs")}\" >nul 2>&1 & rmdir \"{dir}\" >nul 2>&1";
        Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
    }

    // ───────────────────────── quiet (command-line) mode ─────────────────────────

    public static int InstallQuiet(InstallScope scope)
    {
        try
        {
            if (scope == InstallScope.AllUsers && !IsElevated) return ElevatedHelper.RelaunchElevatedAndWait();
            string dir = InstalledDirectory(scope) ?? DefaultDirectory(scope);
            bool startMenu = InstalledDirectory(scope) == null || HasStartMenuShortcut(scope);
            Install(new InstallOptions(scope, dir, startMenu, HasDesktopShortcut(scope)));
            return 0;
        }
        catch (Exception ex) { CrashLog.Write(ex, "Quiet install"); return 1; }
    }

    public static int UninstallQuiet(InstallScope scope)
    {
        try
        {
            if (scope == InstallScope.AllUsers && !IsElevated)
            {
                int rc = ElevatedHelper.RelaunchElevatedAndWait();
                if (rc == 0) ForgetDefaultForCurrentUser(); // the elevated run may be another account
                return rc;
            }
            Uninstall(scope, forgetDefault: true);
            return 0;
        }
        catch (Exception ex) { CrashLog.Write(ex, "Quiet uninstall"); return 1; }
    }

    // ───────────────────────── helpers ─────────────────────────

    static void CreateShortcut(string lnk, string target)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return;
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic sc = shell.CreateShortcut(lnk);
            sc.TargetPath = target;
            sc.WorkingDirectory = Path.GetDirectoryName(target);
            sc.Description = AppInfo.Tagline;
            sc.Save();
        }
        catch { /* shortcuts are optional */ }
    }

    /// <summary>Deletes a shortcut only if it points at a SerPlayer.exe (never a user's unrelated .lnk with the same name).</summary>
    static void DeleteIfOurs(string lnk)
    {
        if (!File.Exists(lnk)) return;
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t != null)
            {
                dynamic shell = Activator.CreateInstance(t)!;
                string target = shell.CreateShortcut(lnk).TargetPath;
                if (!target.EndsWith(ExeName, StringComparison.OrdinalIgnoreCase)) return;
            }
            File.Delete(lnk);
        }
        catch { }
    }

    /// <summary>Asks Explorer to rebuild its icon cache so file icons update without a sign-out.</summary>
    static void RefreshIconCache()
    {
        try
        {
            string tool = Path.Combine(Environment.SystemDirectory, "ie4uinit.exe");
            if (!File.Exists(tool)) return;
            using var p = Process.Start(new ProcessStartInfo(tool, "-show") { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(10_000);
        }
        catch { /* best effort */ }
    }

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
    static void NotifyShell() => SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0, IntPtr.Zero, IntPtr.Zero);
}
