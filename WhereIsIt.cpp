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
#include <unordered_map>
#include <unordered_set>
#include <queue>
#include <thread>
#include <mutex>
#include <condition_variable>

#pragma comment(lib, "comctl32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "ole32.lib")
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
// g_EngineImpl is the concrete in-process engine.
// g_Engine is the interface pointer — swapped to g_PipeEngineImpl when the
// named pipe server is reachable; falls back to g_EngineImpl otherwise.
static IndexingEngine  g_EngineImpl;
static NamedPipeEngine* g_PipeEngineImpl = nullptr;
IIndexEngine* g_Engine = &g_EngineImpl;
std::shared_ptr<std::vector<uint32_t>> g_ActiveResults;

int g_SortColumn = 0;
bool g_SortDescending = false;
int g_SearchEditHeight = 0;

// Search term storage for highlighting
wchar_t g_CurrentQueryW[256] = { 0 };
std::vector<std::wstring> g_HighlightTokens;

// Global UI Context

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

    for (auto& token : tokens) {
        size_t slash = token.find_last_of(L"\\/");
        if (slash != std::wstring::npos) token = token.substr(slash + 1);
        token.erase(std::remove(token.begin(), token.end(), L'*'), token.end());
        token.erase(std::remove(token.begin(), token.end(), L'?'), token.end());
        token.erase(std::remove(token.begin(), token.end(), L'"'), token.end());

        if (!token.empty()) {
            for (auto& c : token) c = towlower(c);
            g_HighlightTokens.push_back(token);
        }
    }
}
HFONT g_FontNormal = NULL;
HFONT g_FontBold = NULL;

#define IDC_STATUS_BAR 122
#define WM_USER_ICON_LOADED (WM_USER + 3)
int g_folderIconIdx = -1;
int g_fileIconIdx = -1;
std::unordered_map<uint32_t, int> g_recordIconCache;
std::unordered_set<uint32_t> g_pendingIconRecords;
std::queue<uint32_t> g_iconRequestQueue;
std::mutex g_iconMutex;
std::condition_variable g_iconCv;
std::thread g_iconWorker;
bool g_iconWorkerRunning = false;

// --- UI FORMATTING HELPERS ---

int GetIconIndex(const std::wstring& /*filename*/, uint16_t attributes, uint32_t recordIdx) {
    if (attributes & FILE_ATTRIBUTE_DIRECTORY) {
        if (g_folderIconIdx != -1) return g_folderIconIdx;
        SHFILEINFOW sfi = { 0 }; 
        SHGetFileInfoW(L"C:\\Windows", FILE_ATTRIBUTE_DIRECTORY, &sfi, sizeof(sfi), SHGFI_USEFILEATTRIBUTES | SHGFI_SYSICONINDEX | SHGFI_SMALLICON);
        return g_folderIconIdx = sfi.iIcon;
    }
    {
        std::lock_guard<std::mutex> lock(g_iconMutex);
        auto it = g_recordIconCache.find(recordIdx);
        if (it != g_recordIconCache.end()) return it->second;
    }
    if (g_fileIconIdx == -1) {
        SHFILEINFOW sfi = { 0 };
        SHGetFileInfoW(L".txt", FILE_ATTRIBUTE_NORMAL, &sfi, sizeof(sfi), SHGFI_USEFILEATTRIBUTES | SHGFI_SYSICONINDEX | SHGFI_SMALLICON);
        g_fileIconIdx = sfi.iIcon;
    }
    {
        std::lock_guard<std::mutex> lock(g_iconMutex);
        if (g_pendingIconRecords.insert(recordIdx).second) {
            g_iconRequestQueue.push(recordIdx);
            g_iconCv.notify_one();
        }
    }
    return g_fileIconIdx;
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

// --- FILE INTERACTION ---

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
    
    wchar_t debugBuf[512];
    swprintf_s(debugBuf, L"[WhereIsIt] ShowContextMenu for path: %s\n", path.c_str());
    Logger::Log(debugBuf);

    if (path.empty()) return;

    PIDLIST_ABSOLUTE pidlFull = nullptr;
    HRESULT hr = SHParseDisplayName(path.c_str(), nullptr, &pidlFull, 0, nullptr);
    if (FAILED(hr)) {
#ifdef _DEBUG
        swprintf_s(debugBuf, L"[WhereIsIt] SHParseDisplayName failed (0x%08X) for path: %s\n", hr, path.c_str());
        Logger::Log(debugBuf);
#endif
        return;
    }

    // Split pidlFull into parent folder + single-item child pidl.
    PUIDLIST_RELATIVE pidlChild = (PUIDLIST_RELATIVE)ILFindLastID(pidlFull);
    PIDLIST_ABSOLUTE pidlParent = ILClone(pidlFull);
    ILRemoveLastID(pidlParent);

    IShellFolder* pDesktop = nullptr;
    if (SUCCEEDED(SHGetDesktopFolder(&pDesktop))) {
        IShellFolder* pParentFolder = nullptr;
        // If pidlParent is empty, it means the item is on the desktop or is a drive root
        if (ILIsEmpty(pidlParent)) {
            pParentFolder = pDesktop;
            pParentFolder->AddRef();
        } else {
            hr = pDesktop->BindToObject(pidlParent, nullptr, IID_PPV_ARGS(&pParentFolder));
        }

        if (SUCCEEDED(hr) && pParentFolder) {
            PCUITEMID_CHILD pidlChildConst = pidlChild;
            IContextMenu* pcm = nullptr;
            hr = pParentFolder->GetUIObjectOf(hwnd, 1, &pidlChildConst, IID_IContextMenu, nullptr, (void**)&pcm);
            if (SUCCEEDED(hr)) {
                pcm->QueryInterface(IID_PPV_ARGS(&g_pCtxMenu3));
                if (!g_pCtxMenu3) pcm->QueryInterface(IID_PPV_ARGS(&g_pCtxMenu2));

                HMENU hMenu = CreatePopupMenu();
                if (SUCCEEDED(pcm->QueryContextMenu(hMenu, 0, 1, 0x7FFF, CMF_NORMAL | CMF_EXPLORE))) {
                    Logger::Log(L"[WhereIsIt] Context menu created, tracking popup...");
                    int cmd = TrackPopupMenu(hMenu, TPM_LEFTALIGN | TPM_TOPALIGN | TPM_RIGHTBUTTON | TPM_RETURNCMD,
                                            screenPt.x, screenPt.y, 0, hwnd, nullptr);
                    if (cmd > 0) {
                        CMINVOKECOMMANDINFO ici = { sizeof(ici) };
                        ici.fMask = 0; ici.hwnd = hwnd; ici.lpVerb = MAKEINTRESOURCEA(cmd - 1);
                        ici.nShow = SW_SHOWNORMAL;
                        hr = pcm->InvokeCommand(&ici);
                        if (FAILED(hr)) {
                            swprintf_s(debugBuf, L"[WhereIsIt] InvokeCommand failed (0x%08X)\n", hr);
                            Logger::Log(debugBuf);
                        }
                    } else {
                        Logger::Log(L"[WhereIsIt] Popup menu closed or failed.");
                    }
                }
                DestroyMenu(hMenu);
                if (g_pCtxMenu3) { g_pCtxMenu3->Release(); g_pCtxMenu3 = nullptr; }
                if (g_pCtxMenu2) { g_pCtxMenu2->Release(); g_pCtxMenu2 = nullptr; }
                pcm->Release();
            } else {
                swprintf_s(debugBuf, L"[WhereIsIt] GetUIObjectOf failed (0x%08X)\n", hr);
                Logger::Log(debugBuf);
            }
            pParentFolder->Release();
        } else {
            swprintf_s(debugBuf, L"[WhereIsIt] BindToObject failed (0x%08X)\n", hr);
            Logger::Log(debugBuf);
        }
        pDesktop->Release();
    }
    ILFree(pidlParent);
    ILFree(pidlFull);
}

// --- CUSTOM DRAW ENGINE (Bolding Match) ---

LRESULT OnCustomDraw(NMLVCUSTOMDRAW* pcd) {
    switch (pcd->nmcd.dwDrawStage) {
    case CDDS_PREPAINT: return CDRF_NOTIFYITEMDRAW;
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

            HIMAGELIST hSIL = ListView_GetImageList(hFileList, LVSIL_SMALL);
            int iconIdx = GetIconIndex(nameStr, rec.FileAttributes, recordIdx);
            ImageList_Draw(hSIL, iconIdx, pcd->nmcd.hdc, rect.left + 2, rect.top + (rect.bottom - rect.top - 16)/2, ILD_TRANSPARENT);
            rect.left += 20; 

            size_t matchPos = std::wstring::npos;
            size_t matchLen = 0;

            if (!g_HighlightTokens.empty()) {
                // StrStrIW: zero-allocation, case-insensitive search directly on the original string.
                for (const auto& token : g_HighlightTokens) {
                    const wchar_t* found = StrStrIW(nameStr.c_str(), token.c_str());
                    if (found) { matchPos = (size_t)(found - nameStr.c_str()); matchLen = token.size(); break; }
                }
            }

            SetBkMode(pcd->nmcd.hdc, TRANSPARENT);
            if (matchPos != std::wstring::npos && matchLen > 0) {
                std::wstring p1 = nameStr.substr(0, matchPos);
                std::wstring p2 = nameStr.substr(matchPos, matchLen);
                std::wstring p3 = nameStr.substr(matchPos + matchLen);

                RECT r1 = rect;
                SelectObject(pcd->nmcd.hdc, g_FontNormal);
                DrawTextW(pcd->nmcd.hdc, p1.c_str(), -1, &r1, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_CALCRECT);
                DrawTextW(pcd->nmcd.hdc, p1.c_str(), -1, &rect, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
                rect.left = r1.right;

                RECT r2 = rect;
                SelectObject(pcd->nmcd.hdc, g_FontBold);
                DrawTextW(pcd->nmcd.hdc, p2.c_str(), -1, &r2, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_CALCRECT);
                DrawTextW(pcd->nmcd.hdc, p2.c_str(), -1, &rect, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
                rect.left = r2.right;

                SelectObject(pcd->nmcd.hdc, g_FontNormal);
                DrawTextW(pcd->nmcd.hdc, p3.c_str(), -1, &rect, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
            } else {
                SelectObject(pcd->nmcd.hdc, g_FontNormal);
                DrawTextW(pcd->nmcd.hdc, nameStr.c_str(), -1, &rect, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
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
    if (pdi->item.mask & LVIF_TEXT) {
        // On-demand lookup — only called for ~30 visible rows at a time, never for the whole result set.
        uint32_t rIdx = (*g_ActiveResults)[pdi->item.iItem];
        switch (pdi->item.iSubItem) {
            case 1: wcsncpy_s(pdi->item.pszText, pdi->item.cchTextMax, g_Engine->GetParentPath(rIdx).c_str(), _TRUNCATE); break;
            case 2: { FileRecord r = g_Engine->GetRecord(rIdx); FormatFileSize(pdi->item.pszText, pdi->item.cchTextMax, g_Engine->GetRecordFileSize(rIdx), r.FileAttributes); break; }
            case 3: { FormatFileTime(pdi->item.pszText, pdi->item.cchTextMax, g_Engine->GetRecordLastModifiedFileTime(rIdx)); break; }
        }
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
        g_Engine->SetNotifyWindow(hWnd);
        g_iconWorkerRunning = true;
        g_iconWorker = std::thread([hWnd]() {
            while (true) {
                uint32_t recordIdx = 0;
                {
                    std::unique_lock<std::mutex> lock(g_iconMutex);
                    g_iconCv.wait(lock, [] { return !g_iconWorkerRunning || !g_iconRequestQueue.empty(); });
                    if (!g_iconWorkerRunning && g_iconRequestQueue.empty()) return;
                    recordIdx = g_iconRequestQueue.front();
                    g_iconRequestQueue.pop();
                }
                std::wstring path = g_Engine->GetFullPath(recordIdx);
                int iconIdx = g_fileIconIdx;
                if (!path.empty()) {
                    SHFILEINFOW sfi = { 0 };
                    if (SHGetFileInfoW(path.c_str(), 0, &sfi, sizeof(sfi), SHGFI_SYSICONINDEX | SHGFI_SMALLICON)) {
                        iconIdx = sfi.iIcon;
                    }
                }
                {
                    std::lock_guard<std::mutex> lock(g_iconMutex);
                    g_recordIconCache[recordIdx] = iconIdx;
                    g_pendingIconRecords.erase(recordIdx);
                }
                PostMessage(hWnd, WM_USER_ICON_LOADED, (WPARAM)recordIdx, 0);
            }
        });

        // Cache font height once for vertical centering performance in WM_SIZE.
        HDC hdc = GetDC(hSearchEdit);
        HFONT hFont = (HFONT)SendMessage(hSearchEdit, WM_GETFONT, 0, 0);
        HFONT hOldFont = (HFONT)SelectObject(hdc, hFont);
        TEXTMETRIC tm;
        GetTextMetrics(hdc, &tm);
        SelectObject(hdc, hOldFont);
        ReleaseDC(hSearchEdit, hdc);
        g_SearchEditHeight = tm.tmHeight;

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
        if (g_CurrentQueryW[0] != 0) {
            std::wstring status = FormatNumberWithCommas(g_ActiveResults ? g_ActiveResults->size() : 0) + L" objects";
            SendMessage(hStatusBar, SB_SETTEXT, 0, (LPARAM)status.c_str());
        } else SendMessage(hStatusBar, SB_SETTEXT, 0, (LPARAM)g_Engine->GetCurrentStatus().c_str());
        break;
    case WM_USER_SEARCH_FINISHED:
        // CRITICAL: This handler must be O(1) — it runs on the UI thread and blocks keystroke processing.
        // Do NOT iterate over results here. Only swap the pointer and tell the list view the new count.
        g_ActiveResults = g_Engine->GetSearchResults();
        if (g_ActiveResults) {
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
            if (g_CurrentQueryW[0] != 0) {
                std::wstring status = FormatNumberWithCommas(count) + L" objects";
                SendMessage(hStatusBar, SB_SETTEXT, 0, (LPARAM)status.c_str());
            } else {
                SendMessage(hStatusBar, SB_SETTEXT, 0, (LPARAM)g_Engine->GetCurrentStatus().c_str());
            }
        }
        break;
    case WM_USER_ICON_LOADED:
        if (g_ActiveResults) {
            uint32_t recordIdx = (uint32_t)wParam;
            for (int i = 0; i < (int)g_ActiveResults->size(); ++i) {
                if ((*g_ActiveResults)[i] == recordIdx) {
                    ListView_RedrawItems(hFileList, i, i);
                    break;
                }
            }
        }
        break;
    case WM_COMMAND:
        if (LOWORD(wParam) == IDC_SEARCH_EDIT && HIWORD(wParam) == EN_CHANGE) {
            GetWindowText(hSearchEdit, g_CurrentQueryW, 256); char queryA[256];
            UpdateHighlightTokens();
            WideCharToMultiByte(CP_UTF8, 0, g_CurrentQueryW, -1, queryA, 256, NULL, NULL); 
            g_Engine->Search(queryA);
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
    case WM_INITMENUPOPUP:
        if (g_pCtxMenu3) { g_pCtxMenu3->HandleMenuMsg(WM_INITMENUPOPUP, wParam, lParam); return 0; }
        if (g_pCtxMenu2) { g_pCtxMenu2->HandleMenuMsg(WM_INITMENUPOPUP, wParam, lParam); return 0; }
        break;
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
            std::queue<uint32_t>().swap(g_iconRequestQueue);
            g_iconWorkerRunning = false;
        }
        g_iconCv.notify_all();
        if (g_iconWorker.joinable()) g_iconWorker.join();
        if (g_FontNormal) DeleteObject(g_FontNormal);
        if (g_FontBold) DeleteObject(g_FontBold); 
        PostQuitMessage(0); 
        break;
    default: return DefWindowProc(hWnd, msg, wParam, lParam);
    }
    return 0;
}

int APIENTRY wWinMain(_In_ HINSTANCE hInstance, _In_opt_ HINSTANCE hPrevInstance, _In_ LPWSTR lpCmdLine, _In_ int nCmdShow) {
    UNREFERENCED_PARAMETER(hPrevInstance);

    // Service command dispatch — must happen before any UI or COM setup.
    if (lpCmdLine && *lpCmdLine) {
        if (wcsstr(lpCmdLine, L"-install"))   return ServiceInstall();
        if (wcsstr(lpCmdLine, L"-uninstall")) return ServiceUninstall();
        if (wcsstr(lpCmdLine, L"-svc"))       return RunAsService();
    }

    // UI mode: try to connect to the named pipe server.
    // If reachable, proxy all search calls through it; otherwise run in-process.
    if (IsNamedPipeServerAvailable()) {
        g_PipeEngineImpl = new NamedPipeEngine();
        g_Engine = g_PipeEngineImpl;
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
    int windowX = (GetSystemMetrics(SM_CXSCREEN) - WINDOW_WIDTH) / 2;
    int windowY = (GetSystemMetrics(SM_CYSCREEN) - WINDOW_HEIGHT) / 2;
    HWND hWnd = CreateWindowW(szWindowClass, szTitle, WS_OVERLAPPEDWINDOW, windowX, windowY, WINDOW_WIDTH, WINDOW_HEIGHT, nullptr, nullptr, hInstance, nullptr);
    if (!hWnd) return FALSE;
    ShowWindow(hWnd, nCmdShow); UpdateWindow(hWnd);
    MSG msg;
    while (GetMessage(&msg, nullptr, 0, 0)) { TranslateMessage(&msg); DispatchMessage(&msg); }
    g_Engine->Stop();
    delete g_PipeEngineImpl;
    g_PipeEngineImpl = nullptr;
    CoUninitialize();
    return (int)msg.wParam;
}
