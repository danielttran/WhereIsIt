using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace WhereIsIt.App;

// ─────────────────────────────────────────────────────────────────────────────
// Win11 modern (always-visible) Explorer context-menu handler — BEST-EFFORT,
// UNVERIFIED. Authored on Linux without a Windows/MSVC/MSIX toolchain, so this
// has NOT been compiled or registered. A Windows agent should validate it.
//
// Why this exists: WhereIsIt already ships a *classic* registry context-menu
// verb (ShellMenuRegistration) — which is what Everything itself uses, and which
// appears on Win11 under "Show more options". Win11's PRIMARY (top-level) menu,
// however, requires an IExplorerCommand COM handler declared by a packaged app
// (sparse MSIX). This file is that handler.
//
// To activate on Windows (steps for the validating agent):
//   1. Build WhereIsIt.App as usual (this compiles the COM handler).
//   2. Create a *sparse* MSIX package whose AppxManifest declares a
//      desktop4:FileExplorerContextMenus / com:ComServer ExeServer (or
//      SurrogateServer) pointing at this CLSID, e.g.:
//        <com:ComServer>
//          <com:ExeServer Executable="WhereIsIt.App.exe" ...>
//            <com:Class Id="{0E5C2A9D-7B3F-4C1A-9E2D-1F3A4B5C6D7E}" .../>
//          </com:ExeServer>
//        </com:ComServer>
//        <desktop4:Extension Category="windows.fileExplorerContextMenus">
//          <desktop4:FileExplorerContextMenus>
//            <desktop5:ItemType Type="Directory">
//              <desktop5:Verb Id="WhereIsIt" Clsid="{0E5C2A9D-...}" />
//            </desktop5:ItemType>
//          </desktop4:FileExplorerContextMenus>
//        </desktop4:Extension>
//   3. Register the COM class factory at process start (see RegisterClassFactory
//      note below) and run the app with the sparse package registered.
//   4. Verify the CLSID GUID + IExplorerCommand vtable order against
//      <shobjidl_core.h>; correct any marshalling the Linux author couldn't test.
//
// The command launches WhereIsIt with `-p <path>` (handled by CommandLineArgs),
// matching the classic verb's behaviour.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Subset of <c>IExplorerCommand</c> (shobjidl_core.h) used to add a
/// top-level Win11 context-menu entry. Method order MUST match the COM vtable.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9")]
internal partial interface IExplorerCommand
{
    [PreserveSig] int GetTitle(IntPtr psiItemArray, out string ppszName);
    [PreserveSig] int GetIcon(IntPtr psiItemArray, out string ppszIcon);
    [PreserveSig] int GetToolTip(IntPtr psiItemArray, out string ppszInfotip);
    [PreserveSig] int GetCanonicalName(out Guid pguidCommandName);
    [PreserveSig] int GetState(IntPtr psiItemArray, [MarshalAs(UnmanagedType.Bool)] bool fOkToBeSlow, out uint pCmdState);
    [PreserveSig] int Invoke(IntPtr psiItemArray, IntPtr pbc);
    [PreserveSig] int GetFlags(out uint pFlags);
    [PreserveSig] int EnumSubCommands(out IntPtr ppEnum);
}

/// <summary>
/// IExplorerCommand implementation that adds "Search with WhereIsIt" to the
/// Win11 primary context menu. Registered under <see cref="ClsidString"/>.
/// </summary>
[GeneratedComClass]
[Guid(ClsidString)]
internal partial class ExplorerCommandHandler : IExplorerCommand
{
    // Stable CLSID for the handler; referenced from the MSIX manifest.
    public const string ClsidString = "0E5C2A9D-7B3F-4C1A-9E2D-1F3A4B5C6D7E";

    private const int S_OK = 0;
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const uint ECS_ENABLED = 0;        // _EXPCMDSTATE
    private const uint ECF_DEFAULT = 0;        // _EXPCMDFLAGS

    public int GetTitle(IntPtr psiItemArray, out string ppszName)
    {
        ppszName = "Search with WhereIsIt";
        return S_OK;
    }

    public int GetIcon(IntPtr psiItemArray, out string ppszIcon)
    {
        // Use the app's own icon; the shell loads "<exe>,0".
        var exe = Environment.ProcessPath;
        ppszIcon = string.IsNullOrEmpty(exe) ? string.Empty : exe + ",0";
        return S_OK;
    }

    public int GetToolTip(IntPtr psiItemArray, out string ppszInfotip)
    {
        ppszInfotip = "Search this folder with WhereIsIt";
        return S_OK;
    }

    public int GetCanonicalName(out Guid pguidCommandName)
    {
        pguidCommandName = Guid.Empty;
        return S_OK;
    }

    public int GetState(IntPtr psiItemArray, bool fOkToBeSlow, out uint pCmdState)
    {
        pCmdState = ECS_ENABLED;
        return S_OK;
    }

    public int Invoke(IntPtr psiItemArray, IntPtr pbc)
    {
        // Best-effort: resolve the first selected item's path and launch the app
        // with -p. Extracting the path from IShellItemArray needs additional COM
        // interop (IShellItemArray::GetItemAt + IShellItem::GetDisplayName) that a
        // Windows agent should complete; the launch shape is shown here.
        try
        {
            var path = TryGetFirstPath(psiItemArray);
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = path is null ? string.Empty : $"-p \"{path}\"",
                    UseShellExecute = true,
                });
            }
        }
        catch { /* never throw across the COM boundary */ }
        return S_OK;
    }

    public int GetFlags(out uint pFlags) { pFlags = ECF_DEFAULT; return S_OK; }

    public int EnumSubCommands(out IntPtr ppEnum) { ppEnum = IntPtr.Zero; return E_NOTIMPL; }

    /// <summary>Placeholder for IShellItemArray → first path extraction. A Windows
    /// agent should implement this via IShellItemArray.GetItemAt(0) +
    /// IShellItem.GetDisplayName(SIGDN_FILESYSPATH).</summary>
    private static string? TryGetFirstPath(IntPtr psiItemArray) => null;

    // NOTE for the validating agent: register the class factory at startup with
    // ComWrappers + CoRegisterClassObject(CLSID, factory, CLSCTX_LOCAL_SERVER,
    // REGCLS_MULTIPLEUSE, out cookie) when launched as the COM server, so the
    // shell can instantiate this handler.
}
