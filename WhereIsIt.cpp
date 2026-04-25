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

// Search term storage for highlighting
wchar_t g_CurrentQueryW[256] = { 0 };
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
    if (path.empty()) return;

    LPITEMIDLIST pidlFull = nullptr;
    if (FAILED(SHParseDisplayName(path.c_str(), nullptr, &pidlFull, 0, nullptr))) return;

    // Split pidlFull into parent folder + single-item child pidl.
    LPITEMIDLIST pidlChild = ILClone(ILFindLastID(pidlFull));
    ILRemoveLastID(pidlFull);

    IShellFolder* pDesktop = nullptr;
    SHGetDesktopFolder(&pDesktop);

    IShellFolder* pParent = nullptr;
    if (pidlFull->mkid.cb == 0) { pParent = pDesktop; pParent->AddRef(); }
    else pDesktop->BindToObject(pidlFull, nullptr, IID_PPV_ARGS(&pParent));

    if (pParent) {
        LPCITEMIDLIST apidl = pidlChild;
        IContextMenu* pcm = nullptr;
        if (SUCCEEDED(pParent->GetUIObjectOf(hwnd, 1, &apidl, IID_IContextMenu, nullptr, (void**)&pcm))) {
            // Prefer IContextMenu3 > IContextMenu2 for owner-draw menu items (icons, etc.).
            pcm->QueryInterface(IID_PPV_ARGS(&g_pCtxMenu3));
            if (!g_pCtxMenu3) pcm->QueryInterface(IID_PPV_ARGS(&g_pCtxMenu2));

            HMENU hMenu = CreatePopupMenu();
            if (SUCCEEDED(pcm->QueryContextMenu(hMenu, 0, 1, 0x7FFF, CMF_NORMAL | CMF_EXPLORE))) {
                int cmd = TrackPopupMenu(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON,
                                        screenPt.x, screenPt.y, 0, hwnd, nullptr);
                if (cmd > 0) {
                    CMINVOKECOMMANDINFO ici = { sizeof(ici), 0, hwnd,
                        MAKEINTRESOURCEA(cmd - 1), nullptr, nullptr, SW_SHOWNORMAL };
                    pcm->InvokeCommand(&ici);
                }
            }
            DestroyMenu(hMenu);

            if (g_pCtxMenu3) { g_pCtxMenu3->Release(); g_pCtxMenu3 = nullptr; }
            if (g_pCtxMenu2) { g_pCtxMenu2->Release(); g_pCtxMenu2 = nullptr; }
            pcm->Release();
        }
        pParent->Release();
    }

    pDesktop->Release();
    ILFree(pidlChild);
    ILFree(pidlFull);
}

// --- CUSTOM DRAW ENGINE (Bolding Match) ---

LRESULT OnCustomDraw(NMLVCUSTOMDRAW* pcd) {
    switch (pcd->nmcd.dwDrawStage) {
    case CDDS_PREPAINT: return CDRF_NOTIFYITEMDRAW;
    case CDDS_ITEMPREPAINT: return CDRF_NOTIFYSUBITEMDRAW;
    case CDDS_ITEMPREPAINT | CDDS_SUBITEM:
        if (pcd->iSubItem == 0) {
            if (!g_ActiveResults || pcd->nmcd.dwItemSpec >= g_ActiveResults->size()) return CDRF_DODEFAULT;

            uint32_t recordIdx = (*g_ActiveResults)[pcd->nmcd.dwItemSpec];
            const auto& rec = g_Engine.GetRecords()[recordIdx];
            const char* nameA = g_Engine.GetFileNamePool().GetString(rec.NamePoolOffset);
            wchar_t nameW[MAX_PATH];
            MultiByteToWideChar(CP_UTF8, 0, nameA, -1, nameW, MAX_PATH);

            RECT rect;
            ListView_GetSubItemRect(hFileList, (int)pcd->nmcd.dwItemSpec, 0, LVIR_BOUNDS, &rect);
            
            bool isSelected = (ListView_GetItemState(hFileList, (int)pcd->nmcd.dwItemSpec, LVIS_SELECTED) & LVIS_SELECTED);
            FillRect(pcd->nmcd.hdc, &rect, GetSysColorBrush(isSelected ? COLOR_HIGHLIGHT : COLOR_WINDOW));
            SetTextColor(pcd->nmcd.hdc, GetSysColor(isSelected ? COLOR_HIGHLIGHTTEXT : COLOR_WINDOWTEXT));

            HIMAGELIST hSIL = ListView_GetImageList(hFileList, LVSIL_SMALL);
            int iconIdx = GetIconIndex(nameW, rec.FileAttributes);
            ImageList_Draw(hSIL, iconIdx, pcd->nmcd.hdc, rect.left + 2, rect.top + (rect.bottom - rect.top - 16)/2, ILD_TRANSPARENT);
            rect.left += 20; 

            std::wstring nameStr = nameW;
            std::wstring searchStr = g_CurrentQueryW;
            size_t matchPos = std::wstring::npos;
            if (!searchStr.empty()) {
                auto it = std::search(nameStr.begin(), nameStr.end(), searchStr.begin(), searchStr.end(),
                    [](wchar_t c1, wchar_t c2) { return towlower(c1) == towlower(c2); });
                if (it != nameStr.end()) matchPos = std::distance(nameStr.begin(), it);
            }

            SetBkMode(pcd->nmcd.hdc, TRANSPARENT);
            if (matchPos != std::wstring::npos) {
                std::wstring p1 = nameStr.substr(0, matchPos);
                std::wstring p2 = nameStr.substr(matchPos, searchStr.length());
                std::wstring p3 = nameStr.substr(matchPos + searchStr.length());

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
                DrawTextW(pcd->nmcd.hdc, nameW, -1, &rect, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
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
    const auto& rec = g_Engine.GetRecords()[rIdx];
    if (pdi->item.mask & LVIF_TEXT) {
        switch (pdi->item.iSubItem) {
            case 1: wcsncpy_s(pdi->item.pszText, pdi->item.cchTextMax, g_Engine.GetParentPath(rIdx).c_str(), _TRUNCATE); break;
            case 2: FormatFileSize(pdi->item.pszText, pdi->item.cchTextMax, rec.FileSize, rec.FileAttributes); break;
            case 3: FormatFileTime(pdi->item.pszText, pdi->item.cchTextMax, rec.LastModified); break;
        }
    }
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
        g_FontNormal = (HFONT)GetStockObject(DEFAULT_GUI_FONT);
        LOGFONT lf; GetObject(g_FontNormal, sizeof(lf), &lf);
        lf.lfWeight = FW_BOLD; g_FontBold = CreateFontIndirect(&lf);
        SendMessage(hSearchEdit, WM_SETFONT, (WPARAM)g_FontNormal, TRUE);
        SetTimer(hWnd, 1, 100, NULL);
        break;
    }
    case WM_TIMER:
        if (wParam == 1) {
            if (g_CurrentQueryW[0] != 0) {
                wchar_t status[64]; swprintf_s(status, L"%zu objects", g_ActiveResults ? g_ActiveResults->size() : 0);
                SendMessage(hStatusBar, SB_SETTEXT, 0, (LPARAM)status);
            } else SendMessage(hStatusBar, SB_SETTEXT, 0, (LPARAM)g_Engine.GetCurrentStatus().c_str());
            if (g_Engine.HasNewResults() || (!g_Engine.IsBusy() && (!g_ActiveResults || g_ActiveResults->empty()))) {
                g_ActiveResults = g_Engine.GetSearchResults();
                ListView_SetItemCount(hFileList, (int)g_ActiveResults->size());
                InvalidateRect(hFileList, NULL, FALSE);
            }
        } else if (wParam == IDT_SEARCH_DEBOUNCE) {
            KillTimer(hWnd, IDT_SEARCH_DEBOUNCE);
            GetWindowText(hSearchEdit, g_CurrentQueryW, 256); char queryA[256];
            WideCharToMultiByte(CP_UTF8, 0, g_CurrentQueryW, -1, queryA, 256, NULL, NULL); 
            g_Engine.Search(queryA);
        }
        break;
    case WM_COMMAND:
        if (LOWORD(wParam) == IDC_SEARCH_EDIT && HIWORD(wParam) == EN_CHANGE) SetTimer(hWnd, IDT_SEARCH_DEBOUNCE, 150, NULL);
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
            else if (header->code == NM_RCLICK) {
                NMITEMACTIVATE* pnm = (NMITEMACTIVATE*)lParam;
                int idx = pnm->iItem >= 0 ? pnm->iItem : GetFirstSelectedIndex();
                if (idx >= 0) {
                    ListView_SetItemState(hFileList, idx, LVIS_SELECTED | LVIS_FOCUSED, LVIS_SELECTED | LVIS_FOCUSED);
                    POINT pt; GetCursorPos(&pt);
                    ShowShellContextMenu(hWnd, idx, pt);
                }
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
    case WM_DESTROY: if (g_FontBold) DeleteObject(g_FontBold); PostQuitMessage(0); break;
    default: return DefWindowProc(hWnd, msg, wParam, lParam);
    }
    return 0;
}

int APIENTRY wWinMain(_In_ HINSTANCE hInstance, _In_opt_ HINSTANCE hPrevInstance, _In_ LPWSTR lpCmdLine, _In_ int nCmdShow) {
    UNREFERENCED_PARAMETER(hPrevInstance); UNREFERENCED_PARAMETER(lpCmdLine);
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
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
