# WhereIsIt

Fast file search for Windows. A modern WinUI 3 shell over a C++ NTFS indexer,
built as a daily-use replacement for voidtools' *Everything*.

## What you get

- **Native indexing** — USN journal + MFT scan via the C++ engine in
  `src/engine/native/`; results stay live as the file system changes.
- **Everything-grade query syntax** — `ext:`, `size:`, `dm:`/`dc:`/`da:`,
  `attrib:`, `child:`/`parent:`, `dupe:`/`sizedupe:`/`namepartdupe:`/
  `attribdupe:`, `content:`, `startwith:`/`endwith:`, `wfn:`/`wholefilename:`,
  `root:`, `empty:`, `len:`, `count:`, `audio:`/`video:`/`doc:`/`pic:`/`exe:`/
  `zip:`, `file:`/`folder:`, `case:`, `regex:`, `word:`, `path:`, `!`/`|`,
  `AND`/`OR`/`NOT`.
- **Everything File List (.efu)** — import and export the voidtools EFU
  format (`ResultExporter.ToEfu`/`ParseEfu`) alongside CSV/TSV, so file lists
  round-trip with Everything itself.
- **Full sort parity** — name, path, size, modified, created, accessed,
  extension/type, attributes. Native index schema v10 stores all three file
  timestamps, so Created and Accessed sorting work in both native and fallback
  modes. Existing v9 indexes rebuild automatically after upgrade.
- **Everything-style menu bar** — `File / Edit / Search / Bookmarks / View /
  Tools / Help`. Match-case, regex, whole-word, match-path are toggles in the
  Search menu; the quick filters (Audio, Video, Document, ...) sit under
  *Search → Quick filter*. Power users from Everything pick it up with no
  retraining.
- **Modern shell** — Mica backdrop, multi-column sortable results, tabbed
  searches, drag-and-drop into Explorer or editors, CSV/TSV/EFU export, search
  history (↑/↓ recall), bookmarks, run-count column, optional Created /
  Accessed columns, type/attribute sorting, standard Explorer-style file
  operations (rename, Recycle Bin, properties), optional run-on-startup,
  configurable hotkey (default
  `Ctrl+Alt+W`), command-line args (`-s "query"`, `-p "path"`), and a
  localhost-only HTTP server for
  cross-device search.
- **Snappy** — 75 ms keystroke throttle, lock-free seq-fenced decorator,
  bounded post-filter scan, and a cached row view-model pool keep typing
  feeling instant even on large indices.

## Architecture

```
+-------------------------------------------------------------+
|  app/WhereIsIt.App        WinUI 3 exe (XAML + bootstrap)    |
|  app/WhereIsIt.App.Core   ViewModels + services (no WinUI)  |
+-----------------------+-------------------------------------+
                        |
                IEngineClient (C# interface)
                        |
            FilteringEngineClient (decorator)
              · QueryParser parses raw query
              · Simplified form forwarded to inner engine
              · Post-filters returned IDs against the full
                ParsedQuery; seq-fenced against fast typing
                        |
            +-----------+---------------------+
            |                                 |
    NativeEngineClient                InProcEngineClient
    (P/Invoke → C++ DLL)              (Directory.EnumerateFiles
            |                          fallback when the DLL
            |                          is absent)
            v
    src/engine/native/                — WhereIsIt.Engine.Native.dll
      WhereIsIt.Engine.Native.{cpp,h} — C-ABI shim consumed by P/Invoke
      cpp/                            — IndexingEngine + RecordPool +
                                        StringPool + QueryEngine +
                                        USN journal reader + drive
                                        enumerator (was src/legacy/
                                        before the 2026-05-13 port)
```

The C++ engine compiles into `WhereIsIt.Engine.Native.dll`. The App's csproj
copies it from `src\engine\native\x64\$(Configuration)\` into the app's bin
output via a `<None>` item with `PreserveNewest`.

## Repo layout

```
app/
  WhereIsIt.App/                 WinUI 3 exe (net10.0-windows10.0.19041.0)
  WhereIsIt.App.Core/            Class library — no WinUI dependencies
src/
  engine/native/                 C++ engine DLL (WhereIsIt.Engine.Native)
    cpp/                         Engine implementation
  pipe/WhereIsIt.Pipe.Client/    IEngineClient contract + stub pipe client
  core/, adapters/win32/,        Empty scaffolds — not built; leftover from
  engine/winrt/, service/        the original layered-port plan
tests/app/                       xUnit (279 passing)
docs/STATUS.md                   Single source of truth for project state
WhereIsIt.slnx                   Solution: 4 C# projects + 1 C++ project
```

## Building

```powershell
# Build everything (App, App.Core, Pipe.Client, Engine.Native, Tests)
dotnet build WhereIsIt.slnx

# Or run the test suite directly:
dotnet test tests\app\WhereIsIt.App.Tests.csproj

# Or build just the C++ engine:
msbuild src\engine\native\WhereIsIt.Engine.Native.vcxproj `
        /p:Configuration=Release /p:Platform=x64
```

**Prerequisites**

- .NET 10 SDK
- Visual Studio with the **Desktop development with C++** workload
  (v145 toolset or newer, Windows 11 SDK 10.0.22621 or newer)
- Windows App SDK 2.0.1

## Running

```powershell
.\app\WhereIsIt.App\bin\Debug\net10.0-windows10.0.19041.0\WhereIsIt.App.exe
```

The app needs Administrator to read the USN journal and MFT on every fixed
volume. Without elevation it falls back to `Directory.EnumerateFiles` and
loses incremental updates and the index speed advantage, but functionally
everything still works.

## Settings file

`%LOCALAPPDATA%\WhereIsIt\settings.json` — scope roots, hotkey, run-on-startup,
column visibility, bookmarks, search history, run counts, and the opt-in HTTP
server's port.

## Tests

```powershell
dotnet test tests\app\WhereIsIt.App.Tests.csproj
```

279 tests across query parsing, the filtering decorator, view-model state,
search history, bookmarks, the export pipeline, the HTTP frontend, run-count
tally, tabs, content/dupe/attrib filters, date filters, hotkey binding
parsing, command-line args, and the throttle/cache perf contract. 56 of
those exercise the live native DLL via P/Invoke against a real temp scope.

## Credits

- App icon: <https://www.flaticon.com/free-icons/magnifying-glass>
