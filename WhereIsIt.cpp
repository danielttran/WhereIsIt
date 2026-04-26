#include "framework.h"
#include "WhereIsIt.h"
#include "Engine.h"
#include <commctrl.h>
#include <shellapi.h>
#include <shlobj.h>
#include <objbase.h>
#include <map>
#include <memory>
#include <string>
#include <algorithm>

#pragma comment(lib, "comctl32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(linker,"\"/manifestdependency:type='win32' name='Microsoft.Windows.Common-Controls' version='6.0.0.0' processorArchitecture='*' publicKeyToken='6595b64144ccf1df' language='*'\"")

#define MAX_LOADSTRING 100
#define IDT_SEARCH_DEBOUNCE 2

// Global UI Context
HINSTANCE hInst;
WCHAR szTitle[MAX_LOADSTRING], szWindowClass[MAX_LOADSTRING];
HWND hSearchEdit, hFileList, hStatusBar;
IndexingEngine g_Engine;
std::shared_ptr<std::vector<uint32_t>> g_ActiveResults;

int g_SortColumn = 0;
bool g_SortDescending = false;

// Search term storage for highlighting
wchar_t g_CurrentQueryW[256] = { 0 };
std::vector<std::wstring> g_HighlightTokens;

// Pre-cached display data: populated at WM_USER_SEARCH_FINISHED, read lock-free from paint callbacks.
// Indexed parallel to g_ActiveResults (same size). Avoids holding m_dataMutex during LVN_GETDISPINFO.
std::vector<std::wstring> g_CachedPaths;       // parent path per result
std::vector<std::wstring> g_CachedNames;       // file name (wide) per result
std::vector<std::wstring> g_CachedNamesLower;  // lower-case name per result, for highlight matching
std::vector<uint16_t>     g_CachedAttribs;     // FileAttributes per result
std::vector<uint64_t>     g_CachedSizes;       // FileSize per result
std::vector<uint64_t>     g_CachedDates;       // LastModified per result

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
std::map<std::wstring, int> g_iconCache;
int g_folderIconIdx = -1;

// --- UI FORMATTING HELPERS ---

int GetIconIndex(const std::wstring& filename, uint16_t attributes) {
    if (attributes & FILE_ATTRIBUTE_DIRECTORY) {
        if (g_folderIconIdx != -1) return g_folderIconIdx;
        SHFILEINFOW sfi = { 0 }; 
        SHGetFileInfoW(L"C:\\Windows", FILE_ATTRIBUTE_DIRECTORY, &sfi, sizeof(sfi), SHGFI_USEFILEATTRIBUTES | SHGFI_SYSICONINDEX | SHGFI_SMALLICON);
        return g_folderIconIdx = sfi.iIcon;
    }
    size_t dotPos = filename.find_last_of(L'.'); 
    std::wstring extension = (dotPos == std::wstring::npos) ? L"" : filename.substr(dotPos);
    auto cached = g_iconCache.find(extension); 
    if (cached != g_iconCache.end()) return cached->second;
    SHFILEINFOW sfi = { 0 }; 
    SHGetFileInfoW(filename.c_str(), FILE_ATTRIBUTE_NORMAL, &sfi, sizeof(sfi), SHGFI_USEFILEATTRIBUTES | SHGFI_SYSICONINDEX | SHGFI_SMALLICON);
    return g_iconCache[extension] = sfi.iIcon;
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
    std::wstring path = g_Engine.GetFullPath((*g_ActiveResults)[listIdx]);
    if (!path.empty()) ShellExecuteW(hwnd, L"open", path.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
}

static void OpenContainingFolder(HWND hwnd, int listIdx) {
    UNREFERENCED_PARAMETER(hwnd);
    if (!g_ActiveResults || listIdx < 0 || listIdx >= (int)g_ActiveResults->size()) return;
    std::wstring path = g_Engine.GetFullPath((*g_ActiveResults)[listIdx]);
    if (path.empty()) return;
    LPITEMIDLIST pidl = nullptr;
    if (SUCCEEDED(SHParseDisplayName(path.c_str(), nullptr, &pidl, 0, nullptr))) {
        SHOpenFolderAndSelectItems(pidl, 0, nullptr, 0);
        ILFree(pidl);
    }
}

static void ShowShellContextMenu(HWND hwnd, int listIdx, POINT screenPt) {
    if (!g_ActiveResults || listIdx < 0 || listIdx >= (int)g_ActiveResults->size()) return;
    std::wstring path = g_Engine.GetFullPath((*g_ActiveResults)[listIdx]);
    
    wchar_t debugBuf[512];
    swprintf_s(debugBuf, L"[WhereIsIt] ShowContextMenu for path: %s\n", path.c_str());
    Logger::Log(debugBuf);

    if (path.empty()) return;

    PIDLIST_ABSOLUTE pidlFull = nullptr;
    HRESULT hr = SHParseDisplayName(path.c_str(), nullptr, &pidlFull, 0, nullptr);
    if (FAILED(hr)) {
        swprintf_s(debugBuf, L"[WhereIsIt] SHParseDisplayName failed (0x%08X) for path: %s\n", hr, path.c_str());
        Logger::Log(debugBuf);
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
            auto [rec, nameStr] = g_Engine.GetRecordAndName(recordIdx);

            RECT rect;
            ListView_GetSubItemRect(hFileList, (int)listIdx, 0, LVIR_BOUNDS, &rect);
            
            bool isSelected = (ListView_GetItemState(hFileList, (int)listIdx, LVIS_SELECTED) & LVIS_SELECTED);
            FillRect(pcd->nmcd.hdc, &rect, GetSysColorBrush(isSelected ? COLOR_HIGHLIGHT : COLOR_WINDOW));
            SetTextColor(pcd->nmcd.hdc, GetSysColor(isSelected ? COLOR_HIGHLIGHTTEXT : COLOR_WINDOWTEXT));

            HIMAGELIST hSIL = ListView_GetImageList(hFileList, LVSIL_SMALL);
            int iconIdx = GetIconIndex(nameStr, rec.FileAttributes);
            ImageList_Draw(hSIL, iconIdx, pcd->nmcd.hdc, rect.left + 2, rect.top + (rect.bottom - rect.top - 16)/2, ILD_TRANSPARENT);
            rect.left += 20; 

            size_t matchPos = std::wstring::npos;
            std::wstring matchedTerm;

            if (!g_HighlightTokens.empty()) {
                // Lower-case name for highlight matching. Only for visible rows, so towlower loop is cheap.
                std::wstring nameLower = nameStr;
                for (auto& c : nameLower) c = towlower(c);
                for (const auto& token : g_HighlightTokens) {
                    size_t pos = nameLower.find(token);
                    if (pos != std::wstring::npos) { matchPos = pos; matchedTerm = token; break; }
                }
            }

            SetBkMode(pcd->nmcd.hdc, TRANSPARENT);
            if (matchPos != std::wstring::npos && !matchedTerm.empty()) {
                std::wstring p1 = nameStr.substr(0, matchPos);
                std::wstring p2 = nameStr.substr(matchPos, matchedTerm.length());
                std::wstring p3 = nameStr.substr(matchPos + matchedTerm.length());

                SelectObject(pcd->nmcd.hdc, g_FontNormal);
                DrawTextW(pcd->nmcd.hdc, p1.c_str(), -1, &rect, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_CALCRECT);
                DrawTextW(pcd->nmcd.hdc, p1.c_str(), -1, &rect, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
                rect.left = rect.right; rect.right = pcd->nmcd.rc.right;

                SelectObject(pcd->nmcd.hdc, g_FontBold);
                DrawTextW(pcd->nmcd.hdc, p2.c_str(), -1, &rect, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_CALCRECT);
                DrawTextW(pcd->nmcd.hdc, p2.c_str(), -1, &rect, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
                rect.left = rect.right; rect.right = pcd->nmcd.rc.right;

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
            case 1: wcsncpy_s(pdi->item.pszText, pdi->item.cchTextMax, g_Engine.GetParentPath(rIdx).c_str(), _TRUNCATE); break;
            case 2: { FileRecord r = g_Engine.GetRecord(rIdx); FormatFileSize(pdi->item.pszText, pdi->item.cchTextMax, r.FileSize, r.FileAttributes); break; }
            case 3: { FileRecord r = g_Engine.GetRecord(rIdx); FormatFileTime(pdi->item.pszText, pdi->item.cchTextMax, r.LastModified); break; }
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
        hSearchEdit = CreateWindowEx(WS_EX_CLIENTEDGE, L"EDIT", L"", WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL, 0, 0, 0, 0, hWnd, (HMENU)IDC_SEARCH_EDIT, hInst, NULL);
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
        g_Engine.SetNotifyWindow(hWnd);
        // No polling timer needed — status bar updates are event-driven via WM_USER_STATUS_CHANGED.
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
        // Engine posted this when status changed — update status bar immediately, no polling.
        if (g_CurrentQueryW[0] != 0) {
            if (g_ActiveResults) {
                std::wstring s = FormatNumberWithCommas(g_ActiveResults->size()) + L" objects";
                SendMessage(hStatusBar, SB_SETTEXT, 0, (LPARAM)s.c_str());
            }
        } else {
            SendMessage(hStatusBar, SB_SETTEXT, 0, (LPARAM)g_Engine.GetCurrentStatus().c_str());
        }
        break;
    case WM_USER_SEARCH_FINISHED:
        // CRITICAL: This handler must be O(1) — it runs on the UI thread and blocks keystroke processing.
        // Do NOT iterate over results here. Only swap the pointer and tell the list view the new count.
        g_ActiveResults = g_Engine.GetSearchResults();
        // Clear display caches — they are no longer pre-built upfront (too expensive for large result sets).
        // OnDisplayInfo/OnCustomDraw use on-demand per-visible-row lookups.
        g_CachedPaths.clear(); g_CachedNames.clear(); g_CachedNamesLower.clear();
        g_CachedAttribs.clear(); g_CachedSizes.clear(); g_CachedDates.clear();
        if (g_ActiveResults) {
            size_t count = g_ActiveResults->size();
            ListView_SetItemCount(hFileList, (int)count);
            InvalidateRect(hFileList, NULL, FALSE);
            if (g_CurrentQueryW[0] != 0) {
                std::wstring status = FormatNumberWithCommas(count) + L" objects";
                SendMessage(hStatusBar, SB_SETTEXT, 0, (LPARAM)status.c_str());
            } else {
                SendMessage(hStatusBar, SB_SETTEXT, 0, (LPARAM)g_Engine.GetCurrentStatus().c_str());
            }
        }
        break;
    case WM_COMMAND:
        if (LOWORD(wParam) == IDC_SEARCH_EDIT && HIWORD(wParam) == EN_CHANGE) {
            GetWindowText(hSearchEdit, g_CurrentQueryW, 256); char queryA[256];
            UpdateHighlightTokens();
            WideCharToMultiByte(CP_UTF8, 0, g_CurrentQueryW, -1, queryA, 256, NULL, NULL); 
            g_Engine.Search(queryA);
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
                
                g_Engine.Sort(key, g_SortDescending);
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
    case WM_SIZE: {
        int w = LOWORD(lParam), h = HIWORD(lParam), sh = 24, m = 5;
        SendMessage(hStatusBar, WM_SIZE, 0, 0); RECT rs; GetWindowRect(hStatusBar, &rs);
        MoveWindow(hSearchEdit, m, m, w - 2 * m, sh, TRUE);
        MoveWindow(hFileList, 0, sh + 2 * m, w, h - (sh + 2 * m) - (rs.bottom - rs.top), TRUE);
        break;
    }
    case WM_DESTROY: 
        KillTimer(hWnd, 1);
        if (g_FontBold) DeleteObject(g_FontBold); 
        PostQuitMessage(0); 
        break;
    default: return DefWindowProc(hWnd, msg, wParam, lParam);
    }
    return 0;
}

int APIENTRY wWinMain(_In_ HINSTANCE hInstance, _In_opt_ HINSTANCE hPrevInstance, _In_ LPWSTR lpCmdLine, _In_ int nCmdShow) {
    UNREFERENCED_PARAMETER(hPrevInstance); UNREFERENCED_PARAMETER(lpCmdLine);
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
    g_Engine.Start(); hInst = hInstance;
    HWND hWnd = CreateWindowW(szWindowClass, szTitle, WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, 0, CW_USEDEFAULT, 0, nullptr, nullptr, hInstance, nullptr);
    if (!hWnd) return FALSE;
    ShowWindow(hWnd, nCmdShow); UpdateWindow(hWnd);
    MSG msg;
    while (GetMessage(&msg, nullptr, 0, 0)) { TranslateMessage(&msg); DispatchMessage(&msg); }
    g_Engine.Stop();
    CoUninitialize();
    return (int)msg.wParam;
}
