#include "framework.h"
#include "WhereIsIt.h"
#include "Engine.h"
#include <commctrl.h>
#include <shellapi.h>
#include <map>
#include <memory>

#pragma comment(lib, "comctl32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(linker,"\"/manifestdependency:type='win32' name='Microsoft.Windows.Common-Controls' version='6.0.0.0' processorArchitecture='*' publicKeyToken='6595b64144ccf1df' language='*'\"")

#define MAX_LOADSTRING 100
#define IDT_SEARCH_DEBOUNCE 2

HINSTANCE hInst;
WCHAR szTitle[MAX_LOADSTRING], szWindowClass[MAX_LOADSTRING];
HWND hSearchEdit, hFileList, hStatusBar;
IndexingEngine g_Engine;
std::shared_ptr<std::vector<uint32_t>> g_ActiveResults;

#define IDC_STATUS_BAR 122
std::map<std::wstring, int> g_iconCache;
int g_folderIconIdx = -1;

int GetIconIndex(const std::wstring& filename, uint16_t attributes) {
    if (attributes & FILE_ATTRIBUTE_DIRECTORY) {
        if (g_folderIconIdx != -1) return g_folderIconIdx;
        SHFILEINFOW sfi = { 0 }; SHGetFileInfoW(L"C:\\Windows", FILE_ATTRIBUTE_DIRECTORY, &sfi, sizeof(sfi), SHGFI_USEFILEATTRIBUTES | SHGFI_SYSICONINDEX | SHGFI_SMALLICON);
        return g_folderIconIdx = sfi.iIcon;
    }
    size_t dot = filename.find_last_of(L'.'); std::wstring ext = (dot == std::wstring::npos) ? L"" : filename.substr(dot);
    auto it = g_iconCache.find(ext); if (it != g_iconCache.end()) return it->second;
    SHFILEINFOW sfi = { 0 }; SHGetFileInfoW(filename.c_str(), FILE_ATTRIBUTE_NORMAL, &sfi, sizeof(sfi), SHGFI_USEFILEATTRIBUTES | SHGFI_SYSICONINDEX | SHGFI_SMALLICON);
    return g_iconCache[ext] = sfi.iIcon;
}

void FormatSize(wchar_t* buf, size_t len, uint64_t size, uint16_t attr) {
    if (attr & FILE_ATTRIBUTE_DIRECTORY) { buf[0] = 0; return; }
    if (size < 1024) { swprintf_s(buf, len, L"%llu B", size); return; }
    double s = (double)size; const wchar_t* units[] = { L"KB", L"MB", L"GB", L"TB" };
    int i = -1; while (s >= 1024 && i < 3) { s /= 1024; i++; }
    swprintf_s(buf, len, L"%.1f %s", s, units[i]);
}

void FormatDateTime(wchar_t* buf, size_t len, uint64_t filetime) {
    if (!filetime) { buf[0] = 0; return; }
    FILETIME ft; ft.dwLowDateTime = (DWORD)filetime; ft.dwHighDateTime = (DWORD)(filetime >> 32);
    SYSTEMTIME st, lst; FileTimeToSystemTime(&ft, &st); SystemTimeToTzSpecificLocalTime(NULL, &st, &lst);
    int hour = lst.wHour % 12; if (hour == 0) hour = 12;
    swprintf_s(buf, len, L"%02d/%02d/%04d %02d:%02d %s", lst.wMonth, lst.wDay, lst.wYear, hour, lst.wMinute, (lst.wHour >= 12 ? L"PM" : L"AM"));
}

ATOM MyRegisterClass(HINSTANCE hInstance);
BOOL InitInstance(HINSTANCE, int);
LRESULT CALLBACK WndProc(HWND, UINT, WPARAM, LPARAM);

int APIENTRY wWinMain(_In_ HINSTANCE hInstance, _In_opt_ HINSTANCE hPrevInstance, _In_ LPWSTR lpCmdLine, _In_ int nCmdShow) {
    UNREFERENCED_PARAMETER(hPrevInstance); UNREFERENCED_PARAMETER(lpCmdLine);
    LoadStringW(hInstance, IDS_APP_TITLE, szTitle, MAX_LOADSTRING);
    LoadStringW(hInstance, IDC_WHEREISIT, szWindowClass, MAX_LOADSTRING);
    MyRegisterClass(hInstance);
    INITCOMMONCONTROLSEX icex = { sizeof(icex), ICC_LISTVIEW_CLASSES | ICC_BAR_CLASSES };
    InitCommonControlsEx(&icex);
    g_Engine.Start();
    if (!InitInstance(hInstance, nCmdShow)) return FALSE;
    HACCEL hAccel = LoadAccelerators(hInstance, MAKEINTRESOURCE(IDC_WHEREISIT));
    MSG msg;
    while (GetMessage(&msg, nullptr, 0, 0)) { if (!TranslateAccelerator(msg.hwnd, hAccel, &msg)) { TranslateMessage(&msg); DispatchMessage(&msg); } }
    g_Engine.Stop();
    return (int)msg.wParam;
}

ATOM MyRegisterClass(HINSTANCE hInstance) {
    WNDCLASSEXW wcex = { sizeof(WNDCLASSEX) };
    wcex.style = CS_HREDRAW | CS_VREDRAW; wcex.lpfnWndProc = WndProc; wcex.hInstance = hInstance;
    wcex.hIcon = LoadIcon(hInstance, MAKEINTRESOURCE(IDI_WHEREISIT)); wcex.hCursor = LoadCursor(nullptr, IDC_ARROW);
    wcex.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1); wcex.lpszMenuName = MAKEINTRESOURCEW(IDC_WHEREISIT);
    wcex.lpszClassName = szWindowClass; wcex.hIconSm = LoadIcon(wcex.hInstance, MAKEINTRESOURCE(IDI_WHEREISIT));
    return RegisterClassExW(&wcex);
}

BOOL InitInstance(HINSTANCE hInstance, int nCmdShow) {
    hInst = hInstance;
    HWND hWnd = CreateWindowW(szWindowClass, szTitle, WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, 0, CW_USEDEFAULT, 0, nullptr, nullptr, hInstance, nullptr);
    if (!hWnd) return FALSE;
    ShowWindow(hWnd, nCmdShow); UpdateWindow(hWnd);
    return TRUE;
}

LRESULT CALLBACK WndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    switch (msg) {
    case WM_CREATE: {
        hSearchEdit = CreateWindowEx(WS_EX_CLIENTEDGE, L"EDIT", L"", WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL, 0, 0, 0, 0, hWnd, (HMENU)IDC_SEARCH_EDIT, hInst, NULL);
        hFileList = CreateWindowEx(0, WC_LISTVIEW, L"", WS_CHILD | WS_VISIBLE | LVS_REPORT | LVS_OWNERDATA | LVS_SHAREIMAGELISTS, 0, 0, 0, 0, hWnd, (HMENU)IDC_FILE_LIST, hInst, NULL);
        SHFILEINFOW sfi = { 0 }; HIMAGELIST hSIL = (HIMAGELIST)SHGetFileInfoW(L"C:\\", 0, &sfi, sizeof(sfi), SHGFI_SYSICONINDEX | SHGFI_SMALLICON);
        if (hSIL) ListView_SetImageList(hFileList, hSIL, LVSIL_SMALL);
        hStatusBar = CreateWindowEx(0, STATUSCLASSNAME, L"Initializing...", WS_CHILD | WS_VISIBLE, 0, 0, 0, 0, hWnd, (HMENU)IDC_STATUS_BAR, hInst, NULL);
        ListView_SetExtendedListViewStyle(hFileList, LVS_EX_FULLROWSELECT | LVS_EX_GRIDLINES | LVS_EX_DOUBLEBUFFER);
        LVCOLUMN lvc = { LVCF_TEXT | LVCF_WIDTH | LVCF_SUBITEM };
        lvc.iSubItem = 0; lvc.pszText = (LPWSTR)L"Name"; lvc.cx = 250; ListView_InsertColumn(hFileList, 0, &lvc);
        lvc.iSubItem = 1; lvc.pszText = (LPWSTR)L"Path"; lvc.cx = 350; ListView_InsertColumn(hFileList, 1, &lvc);
        lvc.iSubItem = 2; lvc.pszText = (LPWSTR)L"Size"; lvc.cx = 100; ListView_InsertColumn(hFileList, 2, &lvc);
        lvc.iSubItem = 3; lvc.pszText = (LPWSTR)L"Date Modified"; lvc.cx = 150; ListView_InsertColumn(hFileList, 3, &lvc);
        HFONT hFont = (HFONT)GetStockObject(DEFAULT_GUI_FONT);
        SendMessage(hSearchEdit, WM_SETFONT, (WPARAM)hFont, TRUE); SendMessage(hFileList, WM_SETFONT, (WPARAM)hFont, TRUE);
        SetTimer(hWnd, 1, 100, NULL);
        g_ActiveResults = std::make_shared<std::vector<uint32_t>>();
        break;
    }
    case WM_TIMER:
        if (wParam == 1) {
            SendMessage(hStatusBar, SB_SETTEXT, 0, (LPARAM)g_Engine.GetStatus().c_str());
            if (g_Engine.HasNewResults()) {
                g_ActiveResults = g_Engine.GetSearchResults();
                ListView_SetItemCount(hFileList, (int)g_ActiveResults->size());
                InvalidateRect(hFileList, NULL, FALSE);
            }
        } else if (wParam == IDT_SEARCH_DEBOUNCE) {
            KillTimer(hWnd, IDT_SEARCH_DEBOUNCE);
            wchar_t qw[256]; GetWindowText(hSearchEdit, qw, 256); char qa[256];
            WideCharToMultiByte(CP_UTF8, 0, qw, -1, qa, 256, NULL, NULL); g_Engine.Search(qa);
        }
        break;
    case WM_NOTIFY: {
        LPNMHDR pnmh = (LPNMHDR)lParam;
        if (pnmh->idFrom == IDC_FILE_LIST && pnmh->code == LVN_GETDISPINFO) {
            NMLVDISPINFO* plvdi = (NMLVDISPINFO*)lParam;
            if (g_ActiveResults && plvdi->item.iItem < (int)g_ActiveResults->size()) {
                uint32_t rIdx = (*g_ActiveResults)[plvdi->item.iItem]; const auto& rec = g_Engine.GetRecords()[rIdx];
                if (plvdi->item.mask & LVIF_TEXT) {
                    if (plvdi->item.iSubItem == 0) MultiByteToWideChar(CP_UTF8, 0, g_Engine.GetPool().GetString(rec.NameOffset), -1, plvdi->item.pszText, plvdi->item.cchTextMax);
                    else if (plvdi->item.iSubItem == 1) wcsncpy_s(plvdi->item.pszText, plvdi->item.cchTextMax, g_Engine.GetParentPath(rIdx).c_str(), _TRUNCATE);
                    else if (plvdi->item.iSubItem == 2) FormatSize(plvdi->item.pszText, plvdi->item.cchTextMax, rec.Size, rec.Attributes);
                    else if (plvdi->item.iSubItem == 3) FormatDateTime(plvdi->item.pszText, plvdi->item.cchTextMax, rec.ModifiedTime);
                }
                if (plvdi->item.mask & LVIF_IMAGE) {
                    wchar_t nameW[MAX_PATH]; MultiByteToWideChar(CP_UTF8, 0, g_Engine.GetPool().GetString(rec.NameOffset), -1, nameW, MAX_PATH);
                    plvdi->item.iImage = GetIconIndex(nameW, rec.Attributes);
                }
            }
        }
        break;
    }
    case WM_SIZE: {
        int w = LOWORD(lParam), h = HIWORD(lParam), sh = 24, m = 5;
        SendMessage(hStatusBar, WM_SIZE, 0, 0); RECT rs; GetWindowRect(hStatusBar, &rs); int sth = rs.bottom - rs.top;
        MoveWindow(hSearchEdit, m, m, w - 2 * m, sh, TRUE);
        MoveWindow(hFileList, 0, sh + 2 * m, w, h - (sh + 2 * m) - sth, TRUE);
        break;
    }
    case WM_COMMAND: {
        int id = LOWORD(wParam), ev = HIWORD(wParam);
        if (id == IDC_SEARCH_EDIT && ev == EN_CHANGE) { SetTimer(hWnd, IDT_SEARCH_DEBOUNCE, 150, NULL); }
        if (id == IDM_EXIT) DestroyWindow(hWnd);
        break;
    }
    case WM_DESTROY: PostQuitMessage(0); break;
    default: return DefWindowProc(hWnd, msg, wParam, lParam);
    }
    return 0;
}
