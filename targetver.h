#pragma once

// Including SDKDDKVer.h defines the highest available Windows platform.
// If you wish to build your application for a previous Windows platform, include WinSDKVer.h and
// set the _WIN32_WINNT macro to the platform you wish to support before including SDKDDKVer.h.
#if defined(_WIN32)
    #if defined(__has_include)
        #if __has_include(<SDKDDKVer.h>)
            #include <SDKDDKVer.h>
        #elif !defined(_WIN32_WINNT)
            #define _WIN32_WINNT 0x0A00
        #endif
    #else
        #include <SDKDDKVer.h>
    #endif
#endif
