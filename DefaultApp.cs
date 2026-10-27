using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SerPlayer;

/// <summary>
/// Default-app helpers. Windows 10/11 don't let programs set the default handler themselves (UserChoice is
/// hash-protected), so we show Windows' own picker preselected on "always", or point the user at Settings.
/// </summary>
static class DefaultApp
{
    public const string Extension = ".ser";

    /// <summary>True when Windows currently opens .ser files with a SerPlayer.exe.</summary>
    public static bool IsDefault()
    {
        try
        {
            uint size = 1024;
            var sb = new StringBuilder((int)size);
            int hr = AssocQueryString(0, 2 /*ASSOCSTR_EXECUTABLE*/, Extension, "open", sb, ref size);
            return hr == 0 && string.Equals(Path.GetFileName(sb.ToString()), "SerPlayer.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Shows Windows' "how do you want to open .ser files" picker with "always use this app" preselected.</summary>
    public static bool ShowPicker(IWin32Window owner)
    {
        const int OAIF_ALLOW_REGISTRATION = 0x1, OAIF_REGISTER_EXT = 0x2, OAIF_FORCE_REGISTRATION = 0x8;
        var info = new OPENASINFO
        {
            pcszFile = Extension,
            pcszClass = null,
            oaifInFlags = OAIF_ALLOW_REGISTRATION | OAIF_REGISTER_EXT | OAIF_FORCE_REGISTRATION,
        };
        try
        {
            int hr = SHOpenWithDialog(owner.Handle, ref info);
            const int HRESULT_CANCELLED = unchecked((int)0x800704C7);
            return hr == 0 || hr == HRESULT_CANCELLED; // cancelling is still a successful showing of the dialog
        }
        catch { return false; }
    }

    /// <summary>Opens Settings → Default apps, on SER Player's page where Windows supports it.</summary>
    public static void OpenSettings()
    {
        foreach (var uri in new[] { "ms-settings:defaultapps?registeredAppUser=SER%20Player", "ms-settings:defaultapps" })
        {
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); return; }
            catch { }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct OPENASINFO
    {
        public string pcszFile;
        public string? pcszClass;
        public int oaifInFlags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHOpenWithDialog(IntPtr hwndParent, ref OPENASINFO oOAI);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int AssocQueryString(int flags, int str, string pszAssoc, string? pszExtra, StringBuilder pszOut, ref uint pcchOut);
}
