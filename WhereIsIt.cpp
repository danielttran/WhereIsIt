#include "framework.h"
#include "WhereIsIt.h"
#include "Engine.h"
#include "ServiceIPC.h"
#include <commctrl.h>
#include <shellapi.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <objbase.h>
#pragma comment(lib, "shlwapi.lib")
#include <memory>
#include <string>
#include <list>
#include <unordered_map>
#include <unordered_set>
#include <list>
#include "StringUtils.h"
#include <queue>
#include <thread>
#include <mutex>
#include <condition_variable>

#pragma comment(lib, "comctl32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "Msimg32.lib")
#pragma comment(linker,"\"/manifestdependency:type='win32' name='Microsoft.Windows.Common-Controls' version='6.0.0.0' processorArchitecture='*' publicKeyToken='6595b64144ccf1df' language='*'\"")

#define MAX_LOADSTRING 100
#define WINDOW_WIDTH 900
#define WINDOW_HEIGHT 1200
#define SEARCH_BAR_HEIGHT 24
constexpr int SEARCH_BAR_MARGIN = 5;

// Global UI Context
HINSTANCE hInst;
WCHAR szTitle[MAX_LOADSTRING], szWindowClass[MAX_LOADSTRING];
HWND hSearchEdit, hFileList, hStatusBar;
// g_Engine is the active engine interface pointer.
// It is set in WinMain before any other code uses it:
//   - pipe reachable  → g_PipeEngineImpl  (NamedPipeEngine, no local scan)
//   - pipe not found  → g_LocalEngineImpl (IndexingEngine,  full local scan)
// IMPORTANT: do NOT make IndexingEngine a global-scope object. Its constructor
// calls CreateFileMappingW / CreateMutexW for Global\ named objects and writes
// zero into the shared record-count atomic. If the service is already running,
// a global IndexingEngine would corrupt the service's live record count on
// every non-admin process start, making all subsequent clients see 0 records.
static IndexingEngine*  g_LocalEngineImpl  = nullptr;  // allocated in WinMain if needed
static NamedPipeEngine* g_PipeEngineImpl   = nullptr;  // allocated in WinMain if needed
IIndexEngine*           g_Engine           = nullptr;  // assigned in WinMain before use
std::shared_ptr<std::vector<uint32_t>> g_ActiveResults;

int g_SortColumn = 0;
bool g_SortDescending = false;
int g_SearchEditHeight = 0;

// Search term storage for highlighting
// Query buffer: 1024 wide chars covers any realistic search term.
// EN_CHANGE handler uses GetWindowTextLength to avoid silent truncation.
static constexpr int kMaxQueryChars = 1024;
wchar_t g_CurrentQueryW[kMaxQueryChars] = { 0 };
std::vector<std::wstring> g_HighlightTokens;

// Global UI Context
std::wstring g_InitialSearchPath;

// --- Search option state ---
bool g_MatchCase       = false;
bool g_MatchWholeWord  = false;
bool g_MatchPath       = false;
bool g_MatchDiacritics = false;
bool g_EnableRegex     = false;
int  g_FileTypeFilter  = IDM_FILTER_EVERYTHING;



// --- TriggerSearch ---
// Centralises every search trigger. Prepends inline flag tokens based on
// the current UI option flags, then sends the combined query to the engine.
static void TriggerSearch() {
    std::string q = WideToUtf8(g_CurrentQueryW);
    // Prepend option flags as inline query tokens.
    // The QueryEngine's BuildQueryPlan already parses these natively.
    if (g_MatchDiacritics) q = "diacritics:true " + q;
    if (g_MatchPath)       q = "matchpath:true " + q;
    if (g_EnableRegex)     q = "regex:true " + q;
    if (g_MatchWholeWord)  q = "word:true " + q;
    if (g_MatchCase)       q = "case:true " + q;
    // File-type filter: prepend a single "extfilt:X" token.
    // BuildQueryPlan strips this into QueryConfig.ExtWhitelist / FolderOnly,
    // so the engine's AVX2 fast loops apply the filter inline with zero AST overhead.
    const char* extFiltName = nullptr;
    if      (g_FileTypeFilter == IDM_FILTER_AUDIO)      extFiltName = "extfilt:audio";
    else if (g_FileTypeFilter == IDM_FILTER_CODE)        extFiltName = "extfilt:code";
    else if (g_FileTypeFilter == IDM_FILTER_COMPRESSED)  extFiltName = "extfilt:compressed";
    else if (g_FileTypeFilter == IDM_FILTER_DOCUMENT)    extFiltName = "extfilt:document";
    else if (g_FileTypeFilter == IDM_FILTER_EXECUTABLE)  extFiltName = "extfilt:executable";
    else if (g_FileTypeFilter == IDM_FILTER_FOLDER)      extFiltName = "extfilt:folder";
    else if (g_FileTypeFilter == IDM_FILTER_PICTURE)     extFiltName = "extfilt:picture";
    else if (g_FileTypeFilter == IDM_FILTER_VIDEO)       extFiltName = "extfilt:video";
    if (extFiltName) q = std::string(extFiltName) + (q.empty() ? "" : " ") + q;
    g_Engine->Search(q);
}

void UpdateHighlightTokens() {
    g_HighlightTokens.clear();
    std::wstring searchStr = g_CurrentQueryW;
    if (searchStr.empty()) return;

    std::vector<std::wstring> tokens;
    size_t start = 0;
    while (start < searchStr.length()) {
        while (start < searchStr.length() && (searchStr[start] == L' ' || searchStr[start] == L'\t')) start++;
        if (start >= searchStr.length()) break;
        size_t end = start;
        while (end < searchStr.length() && searchStr[end] != L' ' && searchStr[end] != L'\t') end++;
        tokens.push_back(searchStr.substr(start, end - start));
        start = end;
    }
    if (tokens.empty()) tokens.push_back(searchStr);

    // Operator keywords that must not be bolded in results.
    static const wchar_t* const kOperators[] = { L"or", L"and", L"not" };

    for (auto& token : tokens) {
        // Strip path prefix — only highlight the filename component.
        size_t slash = token.find_last_of(L"\\/");
        if (slash != std::wstring::npos) token = token.substr(slash + 1);
        token.erase(std::remove(token.begin(), token.end(), L'*'), token.end());
        token.erase(std::remove(token.begin(), token.end(), L'?'), token.end());
        token.erase(std::remove(token.begin(), token.end(), L'"'), token.end());
        if (token.empty()) continue;

        // Lower-case for comparison and storage.
        for (auto& c : token) c = towlower(c);

        // Skip bare operator keywords — they match every word that contains them.
        bool isOperator = false;
        for (const wchar_t* op : kOperators)
            if (token == op) { isOperator = true; break; }
        if (isOperator) continue;

        // Skip engine flag tokens (case:true, extfilt:picture, etc.) that
        // are prepended by TriggerSearch and must not reach the highlighter.
        if (token.find(L':') != std::wstring::npos) continue;

        g_HighlightTokens.push_back(token);
    }
}
HFONT g_FontNormal = NULL;
HFONT g_FontBold = NULL;

#define IDC_STATUS_BAR 122
#define WM_USER_ICON_LOADED (WM_USER + 3)
#define WM_USER_THUMBNAIL_LOADED (WM_USER + 4)
int g_CurrentViewMode = LV_VIEW_DETAILS;
int g_CurrentIconSize = 16;
HIMAGELIST g_hCustomImageList = NULL;
// Persisted view mode (loaded from settings.ini; applied after WM_CREATE).
static int g_LastViewMode = IDM_VIEW_DETAILS;
// Forward declaration — defined after WndProc (after all globals are available).
static void SaveSettings(HWND hWnd);
static void LoadSettings();

// Maximum entries kept in the icon/thumbnail cache.
// Beyond this the image list would consume too much GDI memory
// (e.g. 500 × 256 × 256 × 4 bytes ≈ 128 MB for 256-px thumbnails).
static constexpr int MAX_ICON_CACHE_ENTRIES = 500;

// Monotonically-increasing generation counter.  Bumped whenever the result
// set changes or the view mode switches.  Workers read it before and after
// GetImage(); if it changed while they were decoding, the result is stale
// and is discarded (DeleteObject only) rather than posted to the UI thread.
static std::atomic<uint32_t> g_iconGeneration{ 0 };

int g_folderIconIdx = -1;
int g_fileIconIdx = -1;
std::unordered_map<uint32_t, std::pair<int, std::list<uint32_t>::iterator>> g_recordIconCache;
std::list<uint32_t> g_iconCacheLru;
std::unordered_set<uint32_t> g_pendingIconRecords;
std::deque<uint32_t> g_iconRequestQueue;  // deque so visible items can push_front
std::mutex g_iconMutex;
std::condition_variable g_iconCv;
std::vector<std::thread> g_iconWorkers;
bool g_iconWorkerRunning = false;
HIMAGELIST g_hSIL_SmallCached = NULL;  // cached in CDDS_PREPAINT, valid for one paint pass

// --- UI FORMATTING HELPERS ---

// priority=true  => push_front (visible item, load ASAP)
// priority=false => push_back  (prefetch, load when idle)
static void EnqueueIconRequest(uint32_t recordIdx, bool priority) {
    // Caller must NOT hold g_iconMutex.
    std::lock_guard<std::mutex> lock(g_iconMutex);
    if (g_pendingIconRecords.insert(recordIdx).second) {
        if (priority) g_iconRequestQueue.push_front(recordIdx);
        else          g_iconRequestQueue.push_back(recordIdx);
        g_iconCv.notify_one();
    }
}

static bool IsRecordVisibleNow(uint32_t recordIdx)
{
    if (!hFileList || !g_ActiveResults || g_ActiveResults->empty()) return false;
    int top = ListView_GetTopIndex(hFileList);
    int perPage = ListView_GetCountPerPage(hFileList);
    if (top < 0 || perPage <= 0) return false;
    int bottom = top + perPage + 1;
    if (bottom > static_cast<int>(g_ActiveResults->size()))
        bottom = static_cast<int>(g_ActiveResults->size());
    for (int i = top; i < bottom; ++i) {
        if ((*g_ActiveResults)[i] == recordIdx) return true;
    }
    return false;
}


int GetIconIndex(const std::wstring& /*filename*/, uint16_t attributes, uint32_t recordIdx) {
    if (g_CurrentViewMode == LV_VIEW_DETAILS) {
        if (attributes & FILE_ATTRIBUTE_DIRECTORY)
            return (g_folderIconIdx != -1) ? g_folderIconIdx : 0;
        {
            std::lock_guard<std::mutex> lock(g_iconMutex);
            auto it = g_recordIconCache.find(recordIdx);
            if (it != g_recordIconCache.end()) {
                g_iconCacheLru.splice(g_iconCacheLru.begin(), g_iconCacheLru, it->second.second);
                return it->second.first;
            }
        }
        // Return the generic file icon while the real one loads asynchronously.
        return (g_fileIconIdx != -1) ? g_fileIconIdx : 0;
    } else {
        std::lock_guard<std::mutex> lock(g_iconMutex);
        auto it = g_recordIconCache.find(recordIdx);
        if (it != g_recordIconCache.end()) {
            g_iconCacheLru.splice(g_iconCacheLru.begin(), g_iconCacheLru, it->second.second);
            return it->second.first;
        }
    }

    // Visible item: priority = true so it jumps to the front of the queue.
    EnqueueIconRequest(recordIdx, /*priority=*/true);
    return g_CurrentViewMode == LV_VIEW_DETAILS ? g_fileIconIdx : -1;
}

void FormatFileSize(wchar_t* buffer, size_t bufferLen, uint64_t bytes, uint16_t attributes) {
    if (attributes & FILE_ATTRIBUTE_DIRECTORY) { buffer[0] = 0; return; }
    if (bytes < 1024) { swprintf_s(buffer, bufferLen, L"%llu B", bytes); return; }
    double size = (double)bytes; 
    const wchar_t* units[] = { L"KB", L"MB", L"GB", L"TB" };
    int unitIdx = -1;
    while (size >= 1024 && unitIdx < 3) { size /= 1024; unitIdx++; }
    swprintf_s(buffer, bufferLen, L"%.1f %s", size, units[unitIdx]);
}

void FormatFileTime(wchar_t* buffer, size_t bufferLen, uint64_t filetime) {
    if (!filetime) { buffer[0] = 0; return; }
    FILETIME ft; ft.dwLowDateTime = (DWORD)filetime; ft.dwHighDateTime = (DWORD)(filetime >> 32);
    SYSTEMTIME st, localTime; 
    FileTimeToSystemTime(&ft, &st); 
    SystemTimeToTzSpecificLocalTime(NULL, &st, &localTime);
    int displayHour = localTime.wHour % 12; 
    if (displayHour == 0) displayHour = 12;
    swprintf_s(buffer, bufferLen, L"%02d/%02d/%04d %02d:%02d %s", 
        localTime.wMonth, localTime.wDay, localTime.wYear, 
        displayHour, localTime.wMinute, (localTime.wHour >= 12 ? L"PM" : L"AM"));
}


// Globals kept alive only while a shell context menu's TrackPopupMenu loop is running.
IContextMenu2* g_pCtxMenu2 = nullptr;
IContextMenu3* g_pCtxMenu3 = nullptr;

static int GetFirstSelectedIndex() {
    return ListView_GetNextItem(hFileList, -1, LVNI_SELECTED);
}

static void OpenFile(HWND hwnd, int listIdx) {
    if (!g_ActiveResults || listIdx < 0 || listIdx >= (int)g_ActiveResults->size()) return;
    std::wstring path = g_Engine->GetFullPath((*g_ActiveResults)[listIdx]);
    if (!path.empty()) ShellExecuteW(hwnd, L"open", path.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
}

static void OpenContainingFolder(HWND hwnd, int listIdx) {
    UNREFERENCED_PARAMETER(hwnd);
    if (!g_ActiveResults || listIdx < 0 || listIdx >= (int)g_ActiveResults->size()) return;
    std::wstring path = g_Engine->GetFullPath((*g_ActiveResults)[listIdx]);
    if (path.empty()) return;
    LPITEMIDLIST pidl = nullptr;
    if (SUCCEEDED(SHParseDisplayName(path.c_str(), nullptr, &pidl, 0, nullptr))) {
        SHOpenFolderAndSelectItems(pidl, 0, nullptr, 0);
        ILFree(pidl);
    }
}

static void ShowShellContextMenu(HWND hwnd, int listIdx, POINT screenPt) {
    if (!g_ActiveResults || listIdx < 0 || listIdx >= (int)g_ActiveResults->size()) return;
    std::wstring path = g_Engine->GetFullPath((*g_ActiveResults)[listIdx]);
    if (path.empty()) return;

    PIDLIST_ABSOLUTE pidlFull = nullptr;
    HRESULT hr = SHParseDisplayName(path.c_str(), nullptr, &pidlFull, 0, nullptr);
    if (FAILED(hr)) return;

    // RAII guard ensures pidlFull is always freed, even on early returns.
    struct PidlGuard { PIDLIST_ABSOLUTE p; ~PidlGuard() { if (p) ILFree(p); } };
    PidlGuard fullGuard{ pidlFull };

    // Split pidlFull into parent folder + single-item child pidl.
    // ILClone + ILRemoveLastID — guarded so the clone is always freed.
    PUIDLIST_RELATIVE pidlChild  = (PUIDLIST_RELATIVE)ILFindLastID(pidlFull);
    PIDLIST_ABSOLUTE  pidlParent = ILClone(pidlFull);
    if (!pidlParent) return;
    PidlGuard parentGuard{ pidlParent };
    ILRemoveLastID(pidlParent);

    IShellFolder* pDesktop = nullptr;
    if (FAILED(SHGetDesktopFolder(&pDesktop))) return;

    IShellFolder* pParentFolder = nullptr;
    if (ILIsEmpty(pidlParent)) {
        pParentFolder = pDesktop;
        pParentFolder->AddRef();
    } else {
        hr = pDesktop->BindToObject(pidlParent, nullptr, IID_PPV_ARGS(&pParentFolder));
    }
    pDesktop->Release();
    if (FAILED(hr) || !pParentFolder) return;

    PCUITEMID_CHILD pidlChildConst = pidlChild;
    IContextMenu* pcm = nullptr;
    hr = pParentFolder->GetUIObjectOf(hwnd, 1, &pidlChildConst, IID_IContextMenu, nullptr, (void**)&pcm);
    pParentFolder->Release();
    if (FAILED(hr) || !pcm) return;

    pcm->QueryInterface(IID_PPV_ARGS(&g_pCtxMenu3));
    if (!g_pCtxMenu3) pcm->QueryInterface(IID_PPV_ARGS(&g_pCtxMenu2));

    HMENU hMenu = CreatePopupMenu();
    if (SUCCEEDED(pcm->QueryContextMenu(hMenu, 0, 1, 0x7FFF, CMF_NORMAL | CMF_EXPLORE))) {
        int cmd = TrackPopupMenu(hMenu, TPM_LEFTALIGN | TPM_TOPALIGN | TPM_RIGHTBUTTON | TPM_RETURNCMD,
                                 screenPt.x, screenPt.y, 0, hwnd, nullptr);
        if (cmd > 0) {
            CMINVOKECOMMANDINFO ici = { sizeof(ici) };
            ici.fMask = 0; ici.hwnd = hwnd; ici.lpVerb = MAKEINTRESOURCEA(cmd - 1);
            ici.nShow = SW_SHOWNORMAL;
#ifdef _DEBUG
            hr = pcm->InvokeCommand(&ici);
            if (FAILED(hr)) Logger::Log(L"[WhereIsIt] InvokeCommand failed");
#else
            pcm->InvokeCommand(&ici);
#endif
        }
    }
    DestroyMenu(hMenu);
    if (g_pCtxMenu3) { g_pCtxMenu3->Release(); g_pCtxMenu3 = nullptr; }
    if (g_pCtxMenu2) { g_pCtxMenu2->Release(); g_pCtxMenu2 = nullptr; }
    pcm->Release();
}

// --- CUSTOM DRAW ENGINE (Bolding Match) ---

LRESULT OnCustomDraw(NMLVCUSTOMDRAW* pcd) {
    if (g_CurrentViewMode != LV_VIEW_DETAILS) return CDRF_DODEFAULT;
    switch (pcd->nmcd.dwDrawStage) {
    case CDDS_PREPAINT:
        // Cache the image list once per paint pass — avoids a SendMessage per row.
        g_hSIL_SmallCached = ListView_GetImageList(hFileList, LVSIL_SMALL);
        return CDRF_NOTIFYITEMDRAW;
    case CDDS_ITEMPREPAINT: return CDRF_NOTIFYSUBITEMDRAW;
    case CDDS_ITEMPREPAINT | CDDS_SUBITEM:
        if (pcd->iSubItem == 0) {
            size_t listIdx = pcd->nmcd.dwItemSpec;
            if (!g_ActiveResults || listIdx >= g_ActiveResults->size()) return CDRF_DODEFAULT;

            uint32_t recordIdx = (*g_ActiveResults)[listIdx];
            // GetRecordAndName: single shared_lock acquisition for both record and name.
            // Only called for ~30 visible rows, not all results.
            auto [rec, nameStr] = g_Engine->GetRecordAndName(recordIdx);

            RECT rect;
            ListView_GetSubItemRect(hFileList, (int)listIdx, 0, LVIR_BOUNDS, &rect);
            
            bool isSelected = (ListView_GetItemState(hFileList, (int)listIdx, LVIS_SELECTED) & LVIS_SELECTED);
            FillRect(pcd->nmcd.hdc, &rect, GetSysColorBrush(isSelected ? COLOR_HIGHLIGHT : COLOR_WINDOW));
            SetTextColor(pcd->nmcd.hdc, GetSysColor(isSelected ? COLOR_HIGHLIGHTTEXT : COLOR_WINDOWTEXT));

            HIMAGELIST hSIL = g_hSIL_SmallCached;
            int iconIdx = GetIconIndex(nameStr, rec.FileAttributes, recordIdx);
            if (hSIL && iconIdx >= 0)
                ImageList_Draw(hSIL, iconIdx, pcd->nmcd.hdc, rect.left + 2, rect.top + (rect.bottom - rect.top - 16)/2, ILD_TRANSPARENT);
            rect.left += 20;

            size_t matchPos = std::wstring::npos;
            size_t matchLen = 0;

            if (!g_HighlightTokens.empty()) {
                for (const auto& token : g_HighlightTokens) {
                    const wchar_t* found = StrStrIW(nameStr.c_str(), token.c_str());
                    if (found) { matchPos = (size_t)(found - nameStr.c_str()); matchLen = token.size(); break; }
                }
            }

            SetBkMode(pcd->nmcd.hdc, TRANSPARENT);
            const wchar_t* pName = nameStr.c_str();
            int nameLen = (int)nameStr.size();
            if (matchPos != std::wstring::npos && matchLen > 0) {
                // Draw the three segments using raw pointer + explicit length:
                // no heap allocation, no wstring copies.
                int pre  = (int)matchPos;
                int mid  = (int)matchLen;
                int post = nameLen - pre - mid;

                RECT r1 = rect;
                SelectObject(pcd->nmcd.hdc, g_FontNormal);
                if (pre > 0) {
                    DrawTextW(pcd->nmcd.hdc, pName, pre, &r1, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_CALCRECT);
                    DrawTextW(pcd->nmcd.hdc, pName, pre, &rect, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
                    rect.left = r1.right;
                }

                RECT r2 = rect;
                SelectObject(pcd->nmcd.hdc, g_FontBold);
                DrawTextW(pcd->nmcd.hdc, pName + pre, mid, &r2, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_CALCRECT);
                DrawTextW(pcd->nmcd.hdc, pName + pre, mid, &rect, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
                rect.left = r2.right;

                SelectObject(pcd->nmcd.hdc, g_FontNormal);
                if (post > 0)
                    DrawTextW(pcd->nmcd.hdc, pName + pre + mid, post, &rect, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
            } else {
                SelectObject(pcd->nmcd.hdc, g_FontNormal);
                DrawTextW(pcd->nmcd.hdc, pName, nameLen, &rect, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
            }
            return CDRF_SKIPDEFAULT;
        }
        break;
    }
    return CDRF_DODEFAULT;
}

// --- MESSAGE HANDLERS ---

void OnDisplayInfo(NMLVDISPINFO* pdi) {
    if (!g_ActiveResults || pdi->item.iItem >= (int)g_ActiveResults->size()) return;
    uint32_t rIdx = (*g_ActiveResults)[pdi->item.iItem];

    if (pdi->item.mask & LVIF_TEXT) {
        switch (pdi->item.iSubItem) {
            case 0: {
                if (g_CurrentViewMode != LV_VIEW_DETAILS) {
                    // Icon view: just the filename.
                    auto [rec, nameStr] = g_Engine->GetRecordAndName(rIdx);
                    wcsncpy_s(pdi->item.pszText, pdi->item.cchTextMax, nameStr.c_str(), _TRUNCATE);
                }
                // Details view col 0 is rendered by OnCustomDraw — return nothing here.
                break;
            }
            case 1:
            case 2:
            case 3: {
                // Fetch all four columns atomically in one lock acquisition so that
                // size, attributes, and date are always consistent with each other.
                auto d = g_Engine->GetRowDisplayData(rIdx);
                if (pdi->item.iSubItem == 1) {
                    wcsncpy_s(pdi->item.pszText, pdi->item.cchTextMax, d.ParentPath.c_str(), _TRUNCATE);
                } else if (pdi->item.iSubItem == 2) {
                    FormatFileSize(pdi->item.pszText, pdi->item.cchTextMax, d.FileSize, d.Attributes);
                } else {
                    FormatFileTime(pdi->item.pszText, pdi->item.cchTextMax, d.FileTime);
                }
                break;
            }
        }
    }
    if (pdi->item.mask & LVIF_IMAGE) {
        auto [rec, nameStr] = g_Engine->GetRecordAndName(rIdx);
        pdi->item.iImage = GetIconIndex(nameStr, rec.FileAttributes, rIdx);
    }
}

static void SetSortIcon(int columnIndex, bool descending) {
    HWND hHeader = ListView_GetHeader(hFileList);
    int count = Header_GetItemCount(hHeader);
    for (int i = 0; i < count; i++) {
        HDITEM hdi = { HDI_FORMAT };
        Header_GetItem(hHeader, i, &hdi);
        hdi.fmt &= ~(HDF_SORTUP | HDF_SORTDOWN);
        if (i == columnIndex) hdi.fmt |= (descending ? HDF_SORTDOWN : HDF_SORTUP);
        Header_SetItem(hHeader, i, &hdi);
    }
}

std::wstring FormatNumberWithCommas(size_t n) {
    std::wstring s = std::to_wstring(n);
    int insertPosition = (int)s.length() - 3;
    while (insertPosition > 0) {
        s.insert(insertPosition, L",");
        insertPosition -= 3;
    }
    return s;
}

void SetViewMode(HWND hWnd, int viewId) {
    int newMode = LV_VIEW_ICON;
    int newSize = 256;

    switch (viewId) {
        case IDM_VIEW_EXTRALARGE: newMode = LV_VIEW_ICON; newSize = 256; break;
        case IDM_VIEW_LARGE:      newMode = LV_VIEW_ICON; newSize = 96; break;
        case IDM_VIEW_MEDIUM:     newMode = LV_VIEW_ICON; newSize = 48; break;
        case IDM_VIEW_DETAILS:    newMode = LV_VIEW_DETAILS; newSize = 16; break;
    }

    HMENU hMenu = GetMenu(hWnd);
    CheckMenuRadioItem(hMenu, IDM_VIEW_EXTRALARGE, IDM_VIEW_DETAILS, viewId, MF_BYCOMMAND);
    g_LastViewMode = viewId;  // persist this for settings save

    if (g_CurrentViewMode == newMode && g_CurrentIconSize == newSize) return;

    g_CurrentViewMode = newMode;
    g_CurrentIconSize = newSize;

    {
        std::lock_guard<std::mutex> lock(g_iconMutex);
        g_recordIconCache.clear();
        g_iconCacheLru.clear();
        g_pendingIconRecords.clear();
        g_iconRequestQueue.clear();
    }
    // Bump generation so any in-flight thumbnail decode is discarded when it
    // completes (generation mismatch → DeleteObject only, no PostMessage).
    ++g_iconGeneration;

    if (newMode == LV_VIEW_DETAILS) {
        if (g_hCustomImageList) {
            ImageList_Destroy(g_hCustomImageList);
            g_hCustomImageList = NULL;
        }
        ListView_SetImageList(hFileList, NULL, LVSIL_NORMAL);
        ListView_SetView(hFileList, LV_VIEW_DETAILS);
    } else {
        if (g_hCustomImageList) ImageList_Destroy(g_hCustomImageList);
        g_hCustomImageList = ImageList_Create(newSize, newSize, ILC_COLOR32 | ILC_MASK,
                                              MAX_ICON_CACHE_ENTRIES, 100);
        ListView_SetImageList(hFileList, g_hCustomImageList, LVSIL_NORMAL);
        ListView_SetView(hFileList, LV_VIEW_ICON);
        ListView_SetIconSpacing(hFileList, newSize + 16, newSize + 40);
    }
    InvalidateRect(hFileList, NULL, TRUE);
}

LRESULT CALLBACK WndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    switch (msg) {
    case WM_CREATE: {
        hSearchEdit = CreateWindowEx(0, L"EDIT", L"", WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL, 0, 0, 0, 0, hWnd, (HMENU)IDC_SEARCH_EDIT, hInst, NULL);
        hFileList = CreateWindowEx(0, WC_LISTVIEW, L"", WS_CHILD | WS_VISIBLE | LVS_REPORT | LVS_OWNERDATA | LVS_SHAREIMAGELISTS, 0, 0, 0, 0, hWnd, (HMENU)IDC_FILE_LIST, hInst, NULL);
        SHFILEINFOW sfi = { 0 }; HIMAGELIST hSIL = (HIMAGELIST)SHGetFileInfoW(L"C:\\", 0, &sfi, sizeof(sfi), SHGFI_SYSICONINDEX | SHGFI_SMALLICON);
        if (hSIL) ListView_SetImageList(hFileList, hSIL, LVSIL_SMALL);
        hStatusBar = CreateWindowEx(0, STATUSCLASSNAME, L"Initializing...", WS_CHILD | WS_VISIBLE, 0, 0, 0, 0, hWnd, (HMENU)IDC_STATUS_BAR, hInst, NULL);
        ListView_SetExtendedListViewStyle(hFileList, LVS_EX_FULLROWSELECT | LVS_EX_DOUBLEBUFFER);
        LVCOLUMN lvc = { LVCF_TEXT | LVCF_WIDTH | LVCF_SUBITEM };
        lvc.iSubItem = 0; lvc.pszText = (LPWSTR)L"Name"; lvc.cx = 250; ListView_InsertColumn(hFileList, 0, &lvc);
        lvc.iSubItem = 1; lvc.pszText = (LPWSTR)L"Path"; lvc.cx = 350; ListView_InsertColumn(hFileList, 1, &lvc);
        lvc.iSubItem = 2; lvc.pszText = (LPWSTR)L"Size"; lvc.cx = 100; ListView_InsertColumn(hFileList, 2, &lvc);
        lvc.iSubItem = 3; lvc.pszText = (LPWSTR)L"Date Modified"; lvc.cx = 150; ListView_InsertColumn(hFileList, 3, &lvc);
        SetSortIcon(0, false); 
        g_FontNormal = (HFONT)GetStockObject(DEFAULT_GUI_FONT);
        LOGFONT lf; GetObject(g_FontNormal, sizeof(lf), &lf);
        lf.lfWeight = FW_BOLD; g_FontBold = CreateFontIndirect(&lf);
        SendMessage(hSearchEdit, WM_SETFONT, (WPARAM)g_FontNormal, TRUE);
        // Pre-warm both icon indices on the UI thread before workers start.
        // This avoids a lazy-init race where multiple LVN_GETDISPINFO calls
        // on different threads all see g_fileIconIdx == -1 simultaneously.
        {
            SHFILEINFOW sfi2 = { 0 };
            SHGetFileInfoW(L".txt", FILE_ATTRIBUTE_NORMAL, &sfi2, sizeof(sfi2),
                           SHGFI_USEFILEATTRIBUTES | SHGFI_SYSICONINDEX | SHGFI_SMALLICON);
            g_fileIconIdx = sfi2.iIcon;
            sfi2 = { 0 };
            SHGetFileInfoW(L"C:\\Windows", FILE_ATTRIBUTE_DIRECTORY, &sfi2, sizeof(sfi2),
                           SHGFI_USEFILEATTRIBUTES | SHGFI_SYSICONINDEX | SHGFI_SMALLICON);
            g_folderIconIdx = sfi2.iIcon;
        }
        g_Engine->SetNotifyWindow(hWnd);
        // Start workers BEFORE SetViewMode so the worker threads are alive
        // when SetViewMode bumps g_iconGeneration and may enqueue the first requests.
        g_iconWorkerRunning = true;
        auto workerFn = [hWnd]() {
            (void)CoInitializeEx(NULL, COINIT_APARTMENTTHREADED);  // STA required for IShellItemImageFactory
            // Cache a compatible HDC once per worker thread — avoids GetDC(NULL)/ReleaseDC overhead
            // (which hits the GDI lock) on every single thumbnail compositing call.
            HDC hdcScreen = GetDC(NULL);
            HDC hdcSrc    = CreateCompatibleDC(hdcScreen);
            HDC hdcDst    = CreateCompatibleDC(hdcScreen);
            ReleaseDC(NULL, hdcScreen);

            while (true) {
                uint32_t recordIdx = 0;
                int currentMode = LV_VIEW_DETAILS;
                int currentSize = 16;
                {
                    std::unique_lock<std::mutex> lock(g_iconMutex);
                    g_iconCv.wait(lock, [] { return !g_iconWorkerRunning || !g_iconRequestQueue.empty(); });
                    if (!g_iconWorkerRunning && g_iconRequestQueue.empty()) break;
                    recordIdx = g_iconRequestQueue.front();
                    g_iconRequestQueue.pop_front();
                    currentMode = g_CurrentViewMode;
                    currentSize = g_CurrentIconSize;
                }

                // Visibility culling: skip decode for items that have scrolled off-screen
                // while waiting in the queue — avoids wasting SHGetFileInfoW/GetImage work
                // on rows the user can no longer see.
                {
                    int top    = ListView_GetTopIndex(hFileList);
                    int bottom = top + ListView_GetCountPerPage(hFileList) + 1;
                    auto snap  = g_ActiveResults;  // snapshot shared_ptr (thread-safe refcount)
                    bool visible = false;
                    if (snap) {
                        int n = (int)snap->size();
                        for (int vi = top; vi < bottom && vi < n; ++vi) {
                            if ((*snap)[vi] == recordIdx) { visible = true; break; }
                        }
                    }
                    if (!visible) {
                        std::lock_guard<std::mutex> lk(g_iconMutex);
                        g_pendingIconRecords.erase(recordIdx);
                        continue;
                    }
                }

                // Resolve the full path once — used both for SHGetFileInfoW (details)
                // and SHCreateItemFromParsingName (thumbnails).  Doing it outside the lock
                // means only one shared_lock acquisition per item instead of potentially two.
                std::wstring path = g_Engine->GetFullPath(recordIdx);

                if (currentMode == LV_VIEW_DETAILS) {
                    // --- Details mode: shell icon index (fast, ~1 ms per item) ---
                    int iconIdx = g_fileIconIdx;
                    if (!path.empty()) {
                        SHFILEINFOW sfi = { 0 };
                        if (SHGetFileInfoW(path.c_str(), 0, &sfi, sizeof(sfi), SHGFI_SYSICONINDEX | SHGFI_SMALLICON))
                            iconIdx = sfi.iIcon;
                    }
                    { std::lock_guard<std::mutex> lock(g_iconMutex); g_pendingIconRecords.erase(recordIdx); }
                    PostMessage(hWnd, WM_USER_ICON_LOADED, (WPARAM)recordIdx, (LPARAM)iconIdx);
                } else {
                    if (!IsRecordVisibleNow(recordIdx)) {
                        std::lock_guard<std::mutex> lock(g_iconMutex);
                        g_pendingIconRecords.erase(recordIdx);
                        continue;
                    }
                    // --- Thumbnail mode: two-pass IShellItemImageFactory strategy ---
                    // Pass 1: SIIGBF_THUMBNAILONLY — returns in <2ms if Windows has already
                    //   cached this thumbnail in thumbcache_*.db (e.g. Explorer visited the
                    //   folder).  Fails (E_FAIL) for uncached files — no disk I/O wasted.
                    // Pass 2: SIIGBF_RESIZETOFIT | SIIGBF_BIGGERSIZEOK — full decode path,
                    //   only reached for files Explorer has never thumbnailed.
                    //
                    // Generation check: read g_iconGeneration before AND after GetImage.
                    // If the generation changed while we were blocked decoding, the view
                    // has switched or results changed — discard the bitmap (DeleteObject)
                    // instead of posting it to the UI thread.
                    const uint32_t genBefore = g_iconGeneration.load(std::memory_order_acquire);
                    HBITMAP hRaw = nullptr;
                    if (!path.empty()) {
                        IShellItem* pItem = nullptr;
                        if (SUCCEEDED(SHCreateItemFromParsingName(path.c_str(), nullptr, IID_PPV_ARGS(&pItem)))) {
                            IShellItemImageFactory* pFac = nullptr;
                            if (SUCCEEDED(pItem->QueryInterface(IID_PPV_ARGS(&pFac)))) {
                                SIZE sz = { currentSize, currentSize };
                                // Pass 1: fast cache-hit query (returns immediately if cached).
                                HRESULT hr = pFac->GetImage(sz, SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK, &hRaw);
                                if (FAILED(hr)) {
                                    // Pass 2: full decode (may take 50-300ms for large files).
                                    pFac->GetImage(sz, SIIGBF_RESIZETOFIT | SIIGBF_BIGGERSIZEOK, &hRaw);
                                }
                                pFac->Release();
                            }
                            pItem->Release();
                        }
                    }

                    // Composite premultiplied ARGB bitmap onto opaque white DIB section.
                    // Reuse the per-thread HDC pair — avoids two CreateCompatibleDC / DeleteDC
                    // kernel calls per thumbnail (which previously serialized on the GDI lock).
                    HBITMAP hComposited = nullptr;
                    if (hRaw) {
                        BITMAP srcInfo = {};
                        if (GetObject(hRaw, sizeof(BITMAP), &srcInfo) && srcInfo.bmWidth > 0 && srcInfo.bmHeight > 0) {
                            BITMAPINFO bi = {};
                            bi.bmiHeader.biSize        = sizeof(BITMAPINFOHEADER);
                            bi.bmiHeader.biWidth       = currentSize;
                            bi.bmiHeader.biHeight      = -currentSize;
                            bi.bmiHeader.biPlanes      = 1;
                            bi.bmiHeader.biBitCount    = 32;
                            bi.bmiHeader.biCompression = BI_RGB;
                            void* pBits = nullptr;
                            hComposited = CreateDIBSection(NULL, &bi, DIB_RGB_COLORS, &pBits, NULL, 0);
                            if (hComposited) {
                                HGDIOBJ hOldSrc = SelectObject(hdcSrc, hRaw);
                                HGDIOBJ hOldDst = SelectObject(hdcDst, hComposited);
                                RECT rc = { 0, 0, currentSize, currentSize };
                                FillRect(hdcDst, &rc, (HBRUSH)GetStockObject(WHITE_BRUSH));
                                BLENDFUNCTION bf = { AC_SRC_OVER, 0, 255, AC_SRC_ALPHA };
                                AlphaBlend(hdcDst, 0, 0, currentSize, currentSize,
                                           hdcSrc, 0, 0, srcInfo.bmWidth, srcInfo.bmHeight, bf);
                                SelectObject(hdcSrc, hOldSrc);
                                SelectObject(hdcDst, hOldDst);
                            }
                        }
                        DeleteObject(hRaw);
                    }

                    { std::lock_guard<std::mutex> lock(g_iconMutex); g_pendingIconRecords.erase(recordIdx); }
                    // Only post if the generation hasn't changed since we started decoding.
                    // If it changed the view switched or results refreshed; discard the bitmap.
                    if (hComposited) {
                        if (g_iconGeneration.load(std::memory_order_acquire) == genBefore)
                            PostMessage(hWnd, WM_USER_THUMBNAIL_LOADED, (WPARAM)recordIdx, (LPARAM)hComposited);
                        else
                            DeleteObject(hComposited);  // stale — avoid leaking GDI bitmap
                    }
                }
            }
            DeleteDC(hdcSrc);
            DeleteDC(hdcDst);
            CoUninitialize();
        };
        g_iconWorkers.clear();
        // Thumbnail decoding (IShellItemImageFactory::GetImage) holds the Windows Imaging
        // Component lock internally, so beyond ~4 threads you get diminishing returns and
        // increased context-switch overhead.  Details-mode icon resolution (SHGetFileInfoW)
        // is fast enough that even 2 threads saturates it.
        unsigned int hwc = std::thread::hardware_concurrency();
        unsigned int numWorkers = (hwc < 1u) ? 2u : (hwc > 4u) ? 4u : hwc;
        g_iconWorkers.reserve(numWorkers);
        for (unsigned int i = 0; i < numWorkers; ++i)
            g_iconWorkers.emplace_back(workerFn);

        // Cache font height once for vertical centering performance in WM_SIZE.
        HDC hdc = GetDC(hSearchEdit);
        HFONT hFont = (HFONT)SendMessage(hSearchEdit, WM_GETFONT, 0, 0);
        HFONT hOldFont = (HFONT)SelectObject(hdc, hFont);
        TEXTMETRIC tm;
        GetTextMetrics(hdc, &tm);
        SelectObject(hdc, hOldFont);
        ReleaseDC(hSearchEdit, hdc);
        g_SearchEditHeight = tm.tmHeight;

        if (!g_InitialSearchPath.empty()) {
            SetWindowTextW(hSearchEdit, g_InitialSearchPath.c_str());
        }

        // Restore persisted view mode and sort direction now that all workers are running.
        SetViewMode(hWnd, g_LastViewMode);
        SetSortIcon(g_SortColumn, g_SortDescending);

        // Sync the View menu radio item to the loaded view mode.
        HMENU hMenu = GetMenu(hWnd);
        CheckMenuRadioItem(hMenu, IDM_VIEW_EXTRALARGE, IDM_VIEW_DETAILS, g_LastViewMode, MF_BYCOMMAND);

        break;
    }
    case WM_CONTEXTMENU: {
        if ((HWND)wParam == hFileList) {
            POINT pt = { LOWORD(lParam), HIWORD(lParam) };
            int idx = -1;
            if (pt.x == -1 && pt.y == -1) { 
                idx = GetFirstSelectedIndex();
                if (idx >= 0) {
                    RECT rc; ListView_GetItemRect(hFileList, idx, &rc, LVIR_BOUNDS);
                    pt.x = rc.left; pt.y = rc.top; ClientToScreen(hFileList, &pt);
                }
            } else {
                POINT cpt = pt; ScreenToClient(hFileList, &cpt);
                LVHITTESTINFO ht = { cpt };
                idx = ListView_HitTest(hFileList, &ht);
            }
            if (idx >= 0) {
                ListView_SetItemState(hFileList, idx, LVIS_SELECTED | LVIS_FOCUSED, LVIS_SELECTED | LVIS_FOCUSED);
                ShowShellContextMenu(hWnd, idx, pt);
            }
            return 0;
        }
        break;
    }
    case WM_USER_STATUS_CHANGED:
        // Show object count when a query OR active filter is shaping results.
        if (g_CurrentQueryW[0] != 0 || g_FileTypeFilter != IDM_FILTER_EVERYTHING) {
            std::wstring status = FormatNumberWithCommas(g_ActiveResults ? g_ActiveResults->size() : 0) + L" objects";
            SendMessage(hStatusBar, SB_SETTEXT, 0, (LPARAM)status.c_str());
        } else SendMessage(hStatusBar, SB_SETTEXT, 0, (LPARAM)g_Engine->GetCurrentStatus().c_str());
        break;
    case WM_USER_SEARCH_FINISHED:
        // CRITICAL: This handler must be O(1) — it runs on the UI thread and blocks keystroke processing.
        // Do NOT iterate over results here. Only swap the pointer and tell the list view the new count.
        g_ActiveResults = g_Engine->GetSearchResults();
        if (g_ActiveResults) {
            // Bump generation so workers decoding thumbnails for the previous result set
            // discard their bitmaps instead of posting stale WM_USER_THUMBNAIL_LOADED messages.
            ++g_iconGeneration;
            {
                std::lock_guard<std::mutex> lock(g_iconMutex);
                g_recordIconCache.clear();
                g_iconCacheLru.clear();
                g_pendingIconRecords.clear();
                g_iconRequestQueue.clear();
            }

            size_t count = g_ActiveResults->size();
            ListView_SetItemCountEx(hFileList, (int)count, LVSICF_NOINVALIDATEALL | LVSICF_NOSCROLL);
            if (count > 0) {
                int top = ListView_GetTopIndex(hFileList);
                int bottom = top + ListView_GetCountPerPage(hFileList) + 1;
                if (bottom > (int)count) bottom = (int)count;
                if (top <= bottom - 1) {
                    ListView_RedrawItems(hFileList, top, bottom - 1);
                }
            }
            if (g_CurrentQueryW[0] != 0 || g_FileTypeFilter != IDM_FILTER_EVERYTHING) {
                std::wstring status = FormatNumberWithCommas(count) + L" objects";
                SendMessage(hStatusBar, SB_SETTEXT, 0, (LPARAM)status.c_str());
            } else {
                SendMessage(hStatusBar, SB_SETTEXT, 0, (LPARAM)g_Engine->GetCurrentStatus().c_str());
            }
        }
        break;
    case WM_USER_ICON_LOADED:
        if (g_ActiveResults && g_CurrentViewMode == LV_VIEW_DETAILS) {
            uint32_t recordIdx = (uint32_t)wParam;
            int iconIdx = (int)lParam;
            if (iconIdx == 0) iconIdx = g_fileIconIdx;
            {
                std::lock_guard<std::mutex> lock(g_iconMutex);
                auto it = g_recordIconCache.find(recordIdx);
                if (it != g_recordIconCache.end()) {
                    it->second.first = iconIdx;
                    g_iconCacheLru.splice(g_iconCacheLru.begin(), g_iconCacheLru, it->second.second);
                } else {
                    auto lruIt = g_iconCacheLru.insert(g_iconCacheLru.begin(), recordIdx);
                    g_recordIconCache[recordIdx] = { iconIdx, lruIt };
                }
            }
            // Redraw only the visible rows rather than invalidating the entire control.
            int top    = ListView_GetTopIndex(hFileList);
            int bottom = top + ListView_GetCountPerPage(hFileList) + 1;
            int count  = ListView_GetItemCount(hFileList);
            if (bottom > count) bottom = count;
            if (top <= bottom - 1)
                ListView_RedrawItems(hFileList, top, bottom - 1);
        }
        break;
    case WM_USER_THUMBNAIL_LOADED: {
        uint32_t recordIdx = (uint32_t)wParam;
        HBITMAP hBmp = (HBITMAP)lParam;
        bool bValidSize = false;
        if (hBmp) {
            BITMAP bmp;
            if (GetObject(hBmp, sizeof(BITMAP), &bmp) && bmp.bmWidth == g_CurrentIconSize) {
                bValidSize = true;
            }
        }
        if (bValidSize && g_ActiveResults && g_CurrentViewMode != LV_VIEW_DETAILS && g_hCustomImageList) {
            int currentCount = ImageList_GetImageCount(g_hCustomImageList);
            int imgIdx = -1;
            if (currentCount < MAX_ICON_CACHE_ENTRIES) {
                imgIdx = ImageList_Add(g_hCustomImageList, hBmp, NULL);
                if (imgIdx >= 0) {
                    std::lock_guard<std::mutex> lock(g_iconMutex);
                    auto lruIt = g_iconCacheLru.insert(g_iconCacheLru.begin(), recordIdx);
                    g_recordIconCache[recordIdx] = { imgIdx, lruIt };
                }
            } else {
                std::lock_guard<std::mutex> lock(g_iconMutex);
                if (!g_iconCacheLru.empty()) {
                    uint32_t evictRecord = g_iconCacheLru.back();
                    g_iconCacheLru.pop_back();
                    auto evictIt = g_recordIconCache.find(evictRecord);
                    if (evictIt != g_recordIconCache.end()) {
                        imgIdx = evictIt->second.first;
                        g_recordIconCache.erase(evictIt);
                    }
                }
                if (imgIdx >= 0) {
                    ImageList_Replace(g_hCustomImageList, imgIdx, hBmp, NULL);
                    auto lruIt = g_iconCacheLru.insert(g_iconCacheLru.begin(), recordIdx);
                    g_recordIconCache[recordIdx] = { imgIdx, lruIt };
                }
            }
            // Redraw only the visible rows rather than invalidating the entire control.
            int top    = ListView_GetTopIndex(hFileList);
            int bottom = top + ListView_GetCountPerPage(hFileList) + 1;
            int count  = ListView_GetItemCount(hFileList);
            if (bottom > count) bottom = count;
            if (top <= bottom - 1)
                ListView_RedrawItems(hFileList, top, bottom - 1);
        }
        if (hBmp) DeleteObject(hBmp);
        return 0;
    }
    case WM_COMMAND:
        if (LOWORD(wParam) == IDC_SEARCH_EDIT && HIWORD(wParam) == EN_CHANGE) {
            // Use GetWindowTextLength to avoid silent truncation into a fixed buffer.
            int len = GetWindowTextLengthW(hSearchEdit);
            if (len >= kMaxQueryChars) len = kMaxQueryChars - 1;
            GetWindowTextW(hSearchEdit, g_CurrentQueryW, len + 1);
            g_CurrentQueryW[len] = L'\0';
            UpdateHighlightTokens();
            TriggerSearch();
        } else {
            int wmId = LOWORD(wParam);
            // View mode
            if (wmId >= IDM_VIEW_EXTRALARGE && wmId <= IDM_VIEW_DETAILS) {
                SetViewMode(hWnd, wmId);
                break;
            }
            // Search toggles
            auto ToggleSearchFlag = [&](bool& flag) {
                flag = !flag;
                TriggerSearch();
            };
            switch (wmId) {
            case IDM_SEARCH_MATCHCASE:  ToggleSearchFlag(g_MatchCase);       break;
            case IDM_SEARCH_WHOLEWORD:  ToggleSearchFlag(g_MatchWholeWord);  break;
            case IDM_SEARCH_MATCHPATH:  ToggleSearchFlag(g_MatchPath);       break;
            case IDM_SEARCH_DIACRITICS: ToggleSearchFlag(g_MatchDiacritics); break;
            case IDM_SEARCH_REGEX:      ToggleSearchFlag(g_EnableRegex);     break;
            // File-type filter radio group
            case IDM_FILTER_EVERYTHING:
            case IDM_FILTER_AUDIO:
            case IDM_FILTER_COMPRESSED:
            case IDM_FILTER_DOCUMENT:
            case IDM_FILTER_EXECUTABLE:
            case IDM_FILTER_FOLDER:
            case IDM_FILTER_PICTURE:
            case IDM_FILTER_VIDEO:
            case IDM_FILTER_CODE:
                g_FileTypeFilter = wmId;
                TriggerSearch();
                break;
            // Stub dialogs
            case IDM_SEARCH_ADVANCED:
                MessageBoxW(hWnd, L"Advanced Search — Coming soon.", L"WhereIsIt", MB_OK | MB_ICONINFORMATION);
                break;
            case IDM_SEARCH_ADDFILTER:
                MessageBoxW(hWnd, L"Add to Filters — Coming soon.", L"WhereIsIt", MB_OK | MB_ICONINFORMATION);
                break;
            case IDM_SEARCH_ORGANIZEFILTER:
                MessageBoxW(hWnd, L"Organize Filters — Coming soon.", L"WhereIsIt", MB_OK | MB_ICONINFORMATION);
                break;
            case IDM_ABOUT:
                DialogBox(hInst, MAKEINTRESOURCE(IDD_ABOUTBOX), hWnd, [](HWND hD, UINT m, WPARAM w, LPARAM) -> INT_PTR {
                    if (m == WM_INITDIALOG) return TRUE;
                    if (m == WM_COMMAND && (LOWORD(w) == IDOK || LOWORD(w) == IDCANCEL)) { EndDialog(hD, 0); return TRUE; }
                    return FALSE;
                });
                break;
            case IDM_EXIT:
                DestroyWindow(hWnd);
                break;
            }
        }
        break;
    case WM_NOTIFY: {
        LPNMHDR header = (LPNMHDR)lParam;
        if (header->idFrom == IDC_FILE_LIST) {
            if (header->code == LVN_GETDISPINFO) OnDisplayInfo((NMLVDISPINFO*)lParam);
            else if (header->code == NM_CUSTOMDRAW) return OnCustomDraw((NMLVCUSTOMDRAW*)lParam);
            else if (header->code == NM_DBLCLK) {
                NMITEMACTIVATE* pnm = (NMITEMACTIVATE*)lParam;
                if (pnm->iItem >= 0) OpenFile(hWnd, pnm->iItem);
            }
            else if (header->code == LVN_COLUMNCLICK) {
                NMLISTVIEW* pnmv = (NMLISTVIEW*)lParam;
                if (pnmv->iSubItem == g_SortColumn) g_SortDescending = !g_SortDescending;
                else { g_SortColumn = pnmv->iSubItem; g_SortDescending = false; }
                
                SetSortIcon(g_SortColumn, g_SortDescending);
                
                QuerySortKey key = QuerySortKey::Name;
                if (g_SortColumn == 1) key = QuerySortKey::Path;
                else if (g_SortColumn == 2) key = QuerySortKey::Size;
                else if (g_SortColumn == 3) key = QuerySortKey::Date;
                
                g_Engine->Sort(key, g_SortDescending);
            }
            else if (header->code == LVN_KEYDOWN) {
                NMLVKEYDOWN* pnkd = (NMLVKEYDOWN*)lParam;
                if (pnkd->wVKey == VK_RETURN) {
                    int idx = GetFirstSelectedIndex();
                    if (idx >= 0) {
                        if (GetKeyState(VK_CONTROL) & 0x8000) OpenContainingFolder(hWnd, idx);
                        else OpenFile(hWnd, idx);
                    }
                }
            }
        }
        break;
    }
    case WM_INITMENUPOPUP: {
        // Sync checkmarks and radio bullet for the Search popup before it opens.
        HMENU hPopup = (HMENU)wParam;
        if (GetMenuItemID(hPopup, 0) == IDM_SEARCH_MATCHCASE) {
            // Toggle checkmarks
            CheckMenuItem(hPopup, IDM_SEARCH_MATCHCASE,  g_MatchCase       ? MF_CHECKED : MF_UNCHECKED);
            CheckMenuItem(hPopup, IDM_SEARCH_WHOLEWORD,  g_MatchWholeWord  ? MF_CHECKED : MF_UNCHECKED);
            CheckMenuItem(hPopup, IDM_SEARCH_MATCHPATH,  g_MatchPath       ? MF_CHECKED : MF_UNCHECKED);
            CheckMenuItem(hPopup, IDM_SEARCH_DIACRITICS, g_MatchDiacritics ? MF_CHECKED : MF_UNCHECKED);
            CheckMenuItem(hPopup, IDM_SEARCH_REGEX,      g_EnableRegex     ? MF_CHECKED : MF_UNCHECKED);
            // Radio bullet for active file-type filter
            CheckMenuRadioItem(hPopup, IDM_FILTER_EVERYTHING, IDM_FILTER_CODE, g_FileTypeFilter, MF_BYCOMMAND);
            return 0;
        }
        if (g_pCtxMenu3) { g_pCtxMenu3->HandleMenuMsg(WM_INITMENUPOPUP, wParam, lParam); return 0; }
        if (g_pCtxMenu2) { g_pCtxMenu2->HandleMenuMsg(WM_INITMENUPOPUP, wParam, lParam); return 0; }
        break;
    }
    case WM_MENUCHAR:
        if (g_pCtxMenu3) { LRESULT r = 0; g_pCtxMenu3->HandleMenuMsg2(WM_MENUCHAR, wParam, lParam, &r); return r; }
        break;
    case WM_DRAWITEM:
        if (g_pCtxMenu3) { g_pCtxMenu3->HandleMenuMsg(WM_DRAWITEM, wParam, lParam); return TRUE; }
        if (g_pCtxMenu2) { g_pCtxMenu2->HandleMenuMsg(WM_DRAWITEM, wParam, lParam); return TRUE; }
        break;
    case WM_MEASUREITEM:
        if (g_pCtxMenu3) { g_pCtxMenu3->HandleMenuMsg(WM_MEASUREITEM, wParam, lParam); return TRUE; }
        if (g_pCtxMenu2) { g_pCtxMenu2->HandleMenuMsg(WM_MEASUREITEM, wParam, lParam); return TRUE; }
        break;
    case WM_PAINT: {
        PAINTSTRUCT ps;
        HDC hdc = BeginPaint(hWnd, &ps);
        RECT clientRect;
        GetClientRect(hWnd, &clientRect);
        RECT searchBorderRect = { SEARCH_BAR_MARGIN - 1, SEARCH_BAR_MARGIN - 1, clientRect.right - SEARCH_BAR_MARGIN + 1, SEARCH_BAR_MARGIN + SEARCH_BAR_HEIGHT + 1 };
        // Fill the background white so the edit control's parent area looks like part of the box.
        FillRect(hdc, &searchBorderRect, (HBRUSH)(COLOR_WINDOW + 1));
        FrameRect(hdc, &searchBorderRect, GetSysColorBrush(COLOR_GRAYTEXT));
        EndPaint(hWnd, &ps);
        return 0;
    }
    case WM_SIZE: {
        int w = LOWORD(lParam), h = HIWORD(lParam);
        SendMessage(hStatusBar, WM_SIZE, 0, 0);
        RECT rs;
        GetWindowRect(hStatusBar, &rs);

        // Vertically center the text input. 
        // We use the cached g_SearchEditHeight to avoid DC lookups on every resize.
        int editY = SEARCH_BAR_MARGIN + (SEARCH_BAR_HEIGHT - g_SearchEditHeight) / 2;
        // Left offset 2 to avoid touching the border.
        MoveWindow(hSearchEdit, SEARCH_BAR_MARGIN + 2, editY, w - 2 * SEARCH_BAR_MARGIN - 4, g_SearchEditHeight, TRUE);

        MoveWindow(hFileList, 0, SEARCH_BAR_HEIGHT + 2 * SEARCH_BAR_MARGIN, w, h - (SEARCH_BAR_HEIGHT + 2 * SEARCH_BAR_MARGIN) - (rs.bottom - rs.top), TRUE);
        break;
    }
    case WM_DESTROY:
        {
            std::lock_guard<std::mutex> lock(g_iconMutex);
            std::deque<uint32_t>().swap(g_iconRequestQueue);
            g_iconWorkerRunning = false;
        }
        g_iconCv.notify_all();
        for (auto& t : g_iconWorkers) if (t.joinable()) t.join();
        g_iconWorkers.clear();
        if (g_hCustomImageList) { ImageList_Destroy(g_hCustomImageList); g_hCustomImageList = NULL; }
        // g_FontNormal is a stock object (GetStockObject); do not DeleteObject it.
        if (g_FontBold) DeleteObject(g_FontBold);
        SaveSettings(hWnd);
        PostQuitMessage(0);
        break;
    default: return DefWindowProc(hWnd, msg, wParam, lParam);
    }
    return 0;
}

// --- Persistent Settings (ini file in %LOCALAPPDATA%\WhereIsIt\) ---

static std::wstring GetSettingsPath() {
    wchar_t exePath[MAX_PATH];
    GetModuleFileNameW(NULL, exePath, MAX_PATH);
    std::wstring s(exePath);
    return s.substr(0, s.find_last_of(L'\\')) + L"\\settings.ini";
}

static void LoadSettings() {
    const std::wstring ini = GetSettingsPath();
    const wchar_t* S = ini.c_str();
    auto GetInt = [&](const wchar_t* key, int def) {
        return (int)GetPrivateProfileIntW(L"Search", key, def, S);
    };
    g_MatchCase       = GetInt(L"MatchCase",     0) != 0;
    g_MatchWholeWord  = GetInt(L"MatchWholeWord",0) != 0;
    g_MatchPath       = GetInt(L"MatchPath",     0) != 0;
    g_MatchDiacritics = GetInt(L"Diacritics",    0) != 0;
    g_EnableRegex     = GetInt(L"Regex",         0) != 0;
    int filt = (int)GetPrivateProfileIntW(L"Search", L"FileTypeFilter", IDM_FILTER_EVERYTHING, S);
    // Validate against known filter IDs to guard against a corrupted ini.
    if (filt == IDM_FILTER_EVERYTHING || filt == IDM_FILTER_AUDIO ||
        filt == IDM_FILTER_COMPRESSED || filt == IDM_FILTER_DOCUMENT ||
        filt == IDM_FILTER_EXECUTABLE || filt == IDM_FILTER_FOLDER  ||
        filt == IDM_FILTER_PICTURE    || filt == IDM_FILTER_VIDEO   ||
        filt == IDM_FILTER_CODE)
        g_FileTypeFilter = filt;

    int view = (int)GetPrivateProfileIntW(L"View", L"Mode", IDM_VIEW_DETAILS, S);
    if (view == IDM_VIEW_EXTRALARGE || view == IDM_VIEW_LARGE ||
        view == IDM_VIEW_MEDIUM     || view == IDM_VIEW_DETAILS)
        g_LastViewMode = view;  // applied after hWnd is created
    g_SortColumn     = (int)GetPrivateProfileIntW(L"View", L"SortColumn",    0,     S);
    g_SortDescending = (int)GetPrivateProfileIntW(L"View", L"SortDescending",0,     S) != 0;
    if (g_SortColumn < 0 || g_SortColumn > 3) g_SortColumn = 0;
}

static void SaveSettings(HWND hWnd) {
    const std::wstring ini = GetSettingsPath();
    const wchar_t* S = ini.c_str();
    auto WriteInt = [&](const wchar_t* sec, const wchar_t* key, int val) {
        wchar_t buf[16]; swprintf_s(buf, L"%d", val);
        WritePrivateProfileStringW(sec, key, buf, S);
    };
    WriteInt(L"Search", L"MatchCase",     g_MatchCase       ? 1 : 0);
    WriteInt(L"Search", L"MatchWholeWord",g_MatchWholeWord  ? 1 : 0);
    WriteInt(L"Search", L"MatchPath",     g_MatchPath       ? 1 : 0);
    WriteInt(L"Search", L"Diacritics",    g_MatchDiacritics ? 1 : 0);
    WriteInt(L"Search", L"Regex",         g_EnableRegex     ? 1 : 0);
    WriteInt(L"Search", L"FileTypeFilter",g_FileTypeFilter);
    WriteInt(L"View",   L"Mode",          g_LastViewMode);
    WriteInt(L"View",   L"SortColumn",    g_SortColumn);
    WriteInt(L"View",   L"SortDescending",g_SortDescending  ? 1 : 0);
    UNREFERENCED_PARAMETER(hWnd);
}

int APIENTRY wWinMain(_In_ HINSTANCE hInstance, _In_opt_ HINSTANCE hPrevInstance, _In_ LPWSTR lpCmdLine, _In_ int nCmdShow) {
    UNREFERENCED_PARAMETER(hPrevInstance);

    // Service command dispatch — must happen before any UI or COM setup.
    if (lpCmdLine && *lpCmdLine) {
        if (wcsstr(lpCmdLine, L"-install"))   return ServiceInstall();
        if (wcsstr(lpCmdLine, L"-uninstall")) return ServiceUninstall();
        if (wcsstr(lpCmdLine, L"-svc"))       return RunAsService();
        if (wcsstr(lpCmdLine, L"-register") || wcsstr(lpCmdLine, L"/register")) { RegisterContextMenu(); return 0; }
        if (wcsstr(lpCmdLine, L"-unregister") || wcsstr(lpCmdLine, L"/unregister")) { UnregisterContextMenu(); return 0; }

        std::wstring cmdLineStr(lpCmdLine);
        if (cmdLineStr.front() == L'"' && cmdLineStr.back() == L'"' && cmdLineStr.length() >= 2) {
            g_InitialSearchPath = cmdLineStr.substr(1, cmdLineStr.length() - 2);
        } else {
            g_InitialSearchPath = cmdLineStr;
        }
        if (g_InitialSearchPath.length() > 3 && g_InitialSearchPath.back() == L'\\') {
            g_InitialSearchPath.pop_back();
        }
    }

    // UI mode: try to connect to the named pipe server.
    bool usePipe = IsNamedPipeServerAvailable();
    
    if (!usePipe) {
        if (!IsUserAnAdmin()) {
            int buttonClicked = 0;
            const TASKDIALOG_BUTTON buttons[] = {
                { 100, L"Install Windows Service (Recommended)\nRuns quietly in the background without UAC prompts." },
                { 101, L"Run as Administrator\nRequires a UAC prompt every time." }
            };

            TASKDIALOGCONFIG tdc = { sizeof(TASKDIALOGCONFIG) };
            tdc.dwFlags = TDF_USE_COMMAND_LINKS | TDF_ALLOW_DIALOG_CANCELLATION;
            tdc.pszWindowTitle = L"WhereIsIt";
            tdc.pszMainInstruction = L"Administrator privileges required";
            tdc.pszContent = L"WhereIsIt needs administrator privileges to read NTFS volumes for instant search.";
            tdc.pButtons = buttons;
            tdc.cButtons = _countof(buttons);
            tdc.pszMainIcon = TD_WARNING_ICON;

            HRESULT hr = TaskDialogIndirect(&tdc, &buttonClicked, NULL, NULL);
            if (FAILED(hr) || (buttonClicked != 100 && buttonClicked != 101)) {
                return 0; // Exit if canceled or failed
            }

            wchar_t exePath[MAX_PATH];
            GetModuleFileNameW(NULL, exePath, MAX_PATH);

            if (buttonClicked == 100) {
                // Install Service
                SHELLEXECUTEINFOW sei = { sizeof(SHELLEXECUTEINFOW) };
                sei.lpVerb = L"runas";
                sei.lpFile = exePath;
                sei.lpParameters = L"-install";
                sei.nShow = SW_SHOWNORMAL;
                sei.fMask = SEE_MASK_NOCLOSEPROCESS;
                
                if (ShellExecuteExW(&sei)) {
                    WaitForSingleObject(sei.hProcess, INFINITE);
                    CloseHandle(sei.hProcess);
                    // Give the service a brief moment to start its named pipe server
                    Sleep(1000);
                    usePipe = IsNamedPipeServerAvailable();
                } else {
                    return 0; // Elevation canceled
                }
            } else if (buttonClicked == 101) {
                // Run as Administrator
                SHELLEXECUTEINFOW sei = { sizeof(SHELLEXECUTEINFOW) };
                sei.lpVerb = L"runas";
                sei.lpFile = exePath;
                sei.lpParameters = lpCmdLine;
                sei.nShow = SW_SHOWNORMAL;
                ShellExecuteExW(&sei);
                return 0; // Exit this non-admin instance so the elevated one takes over
            }
        }
    }

    if (usePipe) {
        g_PipeEngineImpl = new NamedPipeEngine();
        g_Engine = g_PipeEngineImpl;
    } else {
        // Only allocate the local (in-process) engine when we actually need it.
        // This is the critical deferral: if we constructed IndexingEngine as a
        // global, its constructor would run before WinMain and corrupt the
        // service's Global\\WhereIsIt_RecordsCount by writing 0 into it.
        g_LocalEngineImpl = new IndexingEngine();
        g_Engine = g_LocalEngineImpl;
    }

    if (FAILED(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE))) return FALSE;
    LoadStringW(hInstance, IDS_APP_TITLE, szTitle, MAX_LOADSTRING);
    LoadStringW(hInstance, IDC_WHEREISIT, szWindowClass, MAX_LOADSTRING);
    WNDCLASSEXW wcex = { sizeof(WNDCLASSEX) };
    wcex.style = CS_HREDRAW | CS_VREDRAW; wcex.lpfnWndProc = WndProc; wcex.hInstance = hInstance;
    wcex.hIcon = LoadIcon(hInstance, MAKEINTRESOURCE(IDI_WHEREISIT)); wcex.hCursor = LoadCursor(nullptr, IDC_ARROW);
    wcex.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1); wcex.lpszMenuName = MAKEINTRESOURCEW(IDC_WHEREISIT);
    wcex.lpszClassName = szWindowClass; wcex.hIconSm = LoadIcon(wcex.hInstance, MAKEINTRESOURCE(IDI_WHEREISIT));
    RegisterClassExW(&wcex);
    INITCOMMONCONTROLSEX icex = { sizeof(INITCOMMONCONTROLSEX), ICC_LISTVIEW_CLASSES | ICC_BAR_CLASSES };
    InitCommonControlsEx(&icex);
    g_Engine->Start(); hInst = hInstance;
    LoadSettings();
    int windowX = (GetSystemMetrics(SM_CXSCREEN) - WINDOW_WIDTH) / 2;
    int windowY = (GetSystemMetrics(SM_CYSCREEN) - WINDOW_HEIGHT) / 2;
    HWND hWnd = CreateWindowW(szWindowClass, szTitle, WS_OVERLAPPEDWINDOW, windowX, windowY, WINDOW_WIDTH, WINDOW_HEIGHT, nullptr, nullptr, hInstance, nullptr);
    if (!hWnd) return FALSE;
    HACCEL hAccel = LoadAccelerators(hInstance, MAKEINTRESOURCE(IDC_WHEREISIT));
    ShowWindow(hWnd, nCmdShow); UpdateWindow(hWnd);
    MSG msg;
    while (GetMessage(&msg, nullptr, 0, 0)) {
        if (!hAccel || !TranslateAcceleratorW(hWnd, hAccel, &msg)) {
            TranslateMessage(&msg);
            DispatchMessage(&msg);
        }
    }
    g_Engine->Stop();
    delete g_PipeEngineImpl;  g_PipeEngineImpl  = nullptr;
    delete g_LocalEngineImpl; g_LocalEngineImpl = nullptr;
    CoUninitialize();
    return (int)msg.wParam;
}
