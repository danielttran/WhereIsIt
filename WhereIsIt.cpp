// WhereIsIt.cpp : Defines the entry point for the application.
//

#include "framework.h"
#include "WhereIsIt.h"
#include "Engine.h"
#include <commctrl.h>

#pragma comment(lib, "comctl32.lib")
#pragma comment(linker,"\"/manifestdependency:type='win32' name='Microsoft.Windows.Common-Controls' version='6.0.0.0' processorArchitecture='*' publicKeyToken='6595b64144ccf1df' language='*'\"")

#define MAX_LOADSTRING 100

// Global Variables:
HINSTANCE hInst;                                // current instance
WCHAR szTitle[MAX_LOADSTRING];                  // The title bar text
WCHAR szWindowClass[MAX_LOADSTRING];            // the main window class name
HWND hSearchEdit;
HWND hFileList;
HWND hStatusBar;
IndexingEngine g_Engine;

#define IDC_STATUS_BAR 122

// Forward declarations of functions included in this code module:
ATOM                MyRegisterClass(HINSTANCE hInstance);
BOOL                InitInstance(HINSTANCE, int);
LRESULT CALLBACK    WndProc(HWND, UINT, WPARAM, LPARAM);
INT_PTR CALLBACK    About(HWND, UINT, WPARAM, LPARAM);

int APIENTRY wWinMain(_In_ HINSTANCE hInstance,
                     _In_opt_ HINSTANCE hPrevInstance,
                     _In_ LPWSTR    lpCmdLine,
                     _In_ int       nCmdShow)
{
    UNREFERENCED_PARAMETER(hPrevInstance);
    UNREFERENCED_PARAMETER(lpCmdLine);

    // Initialize global strings
    LoadStringW(hInstance, IDS_APP_TITLE, szTitle, MAX_LOADSTRING);
    LoadStringW(hInstance, IDC_WHEREISIT, szWindowClass, MAX_LOADSTRING);
    MyRegisterClass(hInstance);

    INITCOMMONCONTROLSEX icex;
    icex.dwSize = sizeof(INITCOMMONCONTROLSEX);
    icex.dwICC = ICC_LISTVIEW_CLASSES | ICC_BAR_CLASSES;
    InitCommonControlsEx(&icex);

    // Start Backend Engine
    g_Engine.Start();

    // Perform application initialization:
    if (!InitInstance (hInstance, nCmdShow))
    {
        return FALSE;
    }

    HACCEL hAccelTable = LoadAccelerators(hInstance, MAKEINTRESOURCE(IDC_WHEREISIT));

    MSG msg;

    // Main message loop:
    while (GetMessage(&msg, nullptr, 0, 0))
    {
        if (!TranslateAccelerator(msg.hwnd, hAccelTable, &msg))
        {
            TranslateMessage(&msg);
            DispatchMessage(&msg);
        }
    }

    // Stop Backend Engine
    g_Engine.Stop();

    return (int) msg.wParam;
}



//
//  FUNCTION: MyRegisterClass()
//
//  PURPOSE: Registers the window class.
//
ATOM MyRegisterClass(HINSTANCE hInstance)
{
    WNDCLASSEXW wcex;

    wcex.cbSize = sizeof(WNDCLASSEX);

    wcex.style          = CS_HREDRAW | CS_VREDRAW;
    wcex.lpfnWndProc    = WndProc;
    wcex.cbClsExtra     = 0;
    wcex.cbWndExtra     = 0;
    wcex.hInstance      = hInstance;
    wcex.hIcon          = LoadIcon(hInstance, MAKEINTRESOURCE(IDI_WHEREISIT));
    wcex.hCursor        = LoadCursor(nullptr, IDC_ARROW);
    wcex.hbrBackground  = (HBRUSH)(COLOR_WINDOW+1);
    wcex.lpszMenuName   = MAKEINTRESOURCEW(IDC_WHEREISIT);
    wcex.lpszClassName  = szWindowClass;
    wcex.hIconSm        = LoadIcon(wcex.hInstance, MAKEINTRESOURCE(IDI_SMALL));

    return RegisterClassExW(&wcex);
}

//
//   FUNCTION: InitInstance(HINSTANCE, int)
//
//   PURPOSE: Saves instance handle and creates main window
//
//   COMMENTS:
//
//        In this function, we save the instance handle in a global variable and
//        create and display the main program window.
//
BOOL InitInstance(HINSTANCE hInstance, int nCmdShow)
{
   hInst = hInstance; // Store instance handle in our global variable

   HWND hWnd = CreateWindowW(szWindowClass, szTitle, WS_OVERLAPPEDWINDOW,
      CW_USEDEFAULT, 0, CW_USEDEFAULT, 0, nullptr, nullptr, hInstance, nullptr);

   if (!hWnd)
   {
      return FALSE;
   }

   ShowWindow(hWnd, nCmdShow);
   UpdateWindow(hWnd);

   return TRUE;
}

//
//  FUNCTION: WndProc(HWND, UINT, WPARAM, LPARAM)
//
//  PURPOSE: Processes messages for the main window.
//
//  WM_COMMAND  - process the application menu
//  WM_PAINT    - Paint the main window
//  WM_DESTROY  - post a quit message and return
//
//
LRESULT CALLBACK WndProc(HWND hWnd, UINT message, WPARAM wParam, LPARAM lParam)
{
    switch (message)
    {
    case WM_CREATE:
        {
            // Create Search Edit Control
            hSearchEdit = CreateWindowEx(WS_EX_CLIENTEDGE, L"EDIT", L"",
                WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL,
                0, 0, 0, 0, hWnd, (HMENU)IDC_SEARCH_EDIT, hInst, NULL);

            // Create File List Control (VIRTUAL LIST)
            hFileList = CreateWindowEx(0, WC_LISTVIEW, L"",
                WS_CHILD | WS_VISIBLE | LVS_REPORT | LVS_OWNERDATA | LVS_SHAREIMAGELISTS,
                0, 0, 0, 0, hWnd, (HMENU)IDC_FILE_LIST, hInst, NULL);

            // Create Status Bar
            hStatusBar = CreateWindowEx(0, STATUSCLASSNAME, L"Initializing...",
                WS_CHILD | WS_VISIBLE, 0, 0, 0, 0, hWnd, (HMENU)IDC_STATUS_BAR, hInst, NULL);

            ListView_SetExtendedListViewStyle(hFileList, LVS_EX_FULLROWSELECT | LVS_EX_GRIDLINES | LVS_EX_DOUBLEBUFFER);

            // Add columns to list view
            LVCOLUMN lvc;
            lvc.mask = LVCF_TEXT | LVCF_WIDTH | LVCF_SUBITEM;
            
            lvc.iSubItem = 0;
            lvc.pszText = (LPWSTR)L"Name";
            lvc.cx = 250;
            ListView_InsertColumn(hFileList, 0, &lvc);

            lvc.iSubItem = 1;
            lvc.pszText = (LPWSTR)L"Path";
            lvc.cx = 400;
            ListView_InsertColumn(hFileList, 1, &lvc);

            lvc.iSubItem = 2;
            lvc.pszText = (LPWSTR)L"Attributes";
            lvc.cx = 100;
            ListView_InsertColumn(hFileList, 2, &lvc);

            // Set font to GUI font
            HFONT hFont = (HFONT)GetStockObject(DEFAULT_GUI_FONT);
            SendMessage(hSearchEdit, WM_SETFONT, (WPARAM)hFont, TRUE);
            SendMessage(hFileList, WM_SETFONT, (WPARAM)hFont, TRUE);

            SetTimer(hWnd, 1, 100, NULL); // Frequent updates for status
        }
        break;

    case WM_TIMER:
        if (wParam == 1) {
            SendMessage(hStatusBar, SB_SETTEXT, 0, (LPARAM)g_Engine.GetStatus().c_str());

            if (!g_Engine.IsBusy()) {
                KillTimer(hWnd, 1);
                // Initial show all
                g_Engine.Search("");
                ListView_SetItemCount(hFileList, (int)g_Engine.GetSearchResults().size());
                InvalidateRect(hFileList, NULL, FALSE);
            }
        }
        break;

    case WM_NOTIFY:
        {
            LPNMHDR pnmh = (LPNMHDR)lParam;
            if (pnmh->idFrom == IDC_FILE_LIST && pnmh->code == LVN_GETDISPINFO) {
                NMLVDISPINFO* plvdi = (NMLVDISPINFO*)lParam;
                if (plvdi->item.mask & LVIF_TEXT) {
                    const auto& results = g_Engine.GetSearchResults();
                    if (plvdi->item.iItem < (int)results.size()) {
                        uint32_t recordIdx = results[plvdi->item.iItem];
                        const auto& record = g_Engine.GetRecords()[recordIdx];

                        if (plvdi->item.iSubItem == 0) { // Name
                            const char* name = g_Engine.GetPool().GetString(record.NameOffset);
                            MultiByteToWideChar(CP_UTF8, 0, name, -1, plvdi->item.pszText, plvdi->item.cchTextMax);
                        }
                        else if (plvdi->item.iSubItem == 1) { // Path (Stub for now)
                            swprintf_s(plvdi->item.pszText, plvdi->item.cchTextMax, L"C:\\... (MFT Index: %u)", record.ParentID);
                        }
                        else if (plvdi->item.iSubItem == 2) { // Attributes
                            swprintf_s(plvdi->item.pszText, plvdi->item.cchTextMax, L"0x%04X", record.Attributes);
                        }
                    }
                }
            }
        }
        break;

    case WM_SIZE:
        {
            int width = LOWORD(lParam);
            int height = HIWORD(lParam);
            int searchHeight = 24;
            int margin = 5;

            // Update status bar
            SendMessage(hStatusBar, WM_SIZE, 0, 0);
            RECT rcStatus;
            GetWindowRect(hStatusBar, &rcStatus);
            int statusHeight = rcStatus.bottom - rcStatus.top;

            MoveWindow(hSearchEdit, margin, margin, width - 2 * margin, searchHeight, TRUE);
            MoveWindow(hFileList, 0, searchHeight + 2 * margin, width, height - (searchHeight + 2 * margin) - statusHeight, TRUE);
        }
        break;

    case WM_COMMAND:
        {
            int wmId = LOWORD(wParam);
            int wmEvent = HIWORD(wParam);

            if (wmId == IDC_SEARCH_EDIT && wmEvent == EN_CHANGE) {
                wchar_t queryW[256];
                GetWindowText(hSearchEdit, queryW, 256);
                
                // Convert search query to UTF-8
                char queryA[256];
                WideCharToMultiByte(CP_UTF8, 0, queryW, -1, queryA, 256, NULL, NULL);

                g_Engine.Search(queryA);
                ListView_SetItemCount(hFileList, (int)g_Engine.GetSearchResults().size());
                ListView_RedrawItems(hFileList, 0, (int)g_Engine.GetSearchResults().size());
            }
            
            // Parse the menu selections:
            switch (wmId)
            {
            case IDM_ABOUT:
                DialogBox(hInst, MAKEINTRESOURCE(IDD_ABOUTBOX), hWnd, About);
                break;
            case IDM_EXIT:
                DestroyWindow(hWnd);
                break;
            default:
                return DefWindowProc(hWnd, message, wParam, lParam);
            }
        }
        break;
    case WM_PAINT:
        {
            PAINTSTRUCT ps;
            HDC hdc = BeginPaint(hWnd, &ps);
            // TODO: Add any drawing code that uses hdc here...
            EndPaint(hWnd, &ps);
        }
        break;
    case WM_DESTROY:
        PostQuitMessage(0);
        break;
    default:
        return DefWindowProc(hWnd, message, wParam, lParam);
    }
    return 0;
}

// Message handler for about box.
INT_PTR CALLBACK About(HWND hDlg, UINT message, WPARAM wParam, LPARAM lParam)
{
    UNREFERENCED_PARAMETER(lParam);
    switch (message)
    {
    case WM_INITDIALOG:
        return (INT_PTR)TRUE;

    case WM_COMMAND:
        if (LOWORD(wParam) == IDOK || LOWORD(wParam) == IDCANCEL)
        {
            EndDialog(hDlg, LOWORD(wParam));
            return (INT_PTR)TRUE;
        }
        break;
    }
    return (INT_PTR)FALSE;
}
