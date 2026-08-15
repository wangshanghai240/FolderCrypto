// pch.h : 预编译头文件
#pragma once

// Windows
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif

#include <windows.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <shellapi.h>
#include <string>
#include <vector>
#include <atlbase.h>
#include <atlcom.h>
#include <atlstr.h>

// ATL helpers
using ATL::CComPtr;
using ATL::CComBSTR;

#pragma comment(lib, "shlwapi.lib")
