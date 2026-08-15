

/* this ALWAYS GENERATED file contains the definitions for the interfaces */


 /* File created by MIDL compiler version 8.01.0628 */
/* at Tue Jan 19 11:14:07 2038
 */
/* Compiler settings for FolderCryptoShellNative.idl:
    Oicf, W1, Zp8, env=Win64 (32b run), target_arch=AMD64 8.01.0628 
    protocol : all , ms_ext, c_ext, robust
    error checks: allocation ref bounds_check enum stub_data 
    VC __declspec() decoration level: 
         __declspec(uuid()), __declspec(selectany), __declspec(novtable)
         DECLSPEC_UUID(), MIDL_INTERFACE()
*/
/* @@MIDL_FILE_HEADING(  ) */



/* verify that the <rpcndr.h> version is high enough to compile this file*/
#ifndef __REQUIRED_RPCNDR_H_VERSION__
#define __REQUIRED_RPCNDR_H_VERSION__ 500
#endif

#include "rpc.h"
#include "rpcndr.h"

#ifndef __RPCNDR_H_VERSION__
#error this stub requires an updated version of <rpcndr.h>
#endif /* __RPCNDR_H_VERSION__ */


#ifndef __FolderCryptoShellNative_h_h__
#define __FolderCryptoShellNative_h_h__

#if defined(_MSC_VER) && (_MSC_VER >= 1020)
#pragma once
#endif

#ifndef DECLSPEC_XFGVIRT
#if defined(_CONTROL_FLOW_GUARD_XFG)
#define DECLSPEC_XFGVIRT(base, func) __declspec(xfg_virtual(base, func))
#else
#define DECLSPEC_XFGVIRT(base, func)
#endif
#endif

/* Forward Declarations */ 

#ifndef __COverlayHandler_FWD_DEFINED__
#define __COverlayHandler_FWD_DEFINED__

#ifdef __cplusplus
typedef class COverlayHandler COverlayHandler;
#else
typedef struct COverlayHandler COverlayHandler;
#endif /* __cplusplus */

#endif 	/* __COverlayHandler_FWD_DEFINED__ */


#ifndef __CEncryptStateHandler_FWD_DEFINED__
#define __CEncryptStateHandler_FWD_DEFINED__

#ifdef __cplusplus
typedef class CEncryptStateHandler CEncryptStateHandler;
#else
typedef struct CEncryptStateHandler CEncryptStateHandler;
#endif /* __cplusplus */

#endif 	/* __CEncryptStateHandler_FWD_DEFINED__ */


#ifndef __CDecryptStateHandler_FWD_DEFINED__
#define __CDecryptStateHandler_FWD_DEFINED__

#ifdef __cplusplus
typedef class CDecryptStateHandler CDecryptStateHandler;
#else
typedef struct CDecryptStateHandler CDecryptStateHandler;
#endif /* __cplusplus */

#endif 	/* __CDecryptStateHandler_FWD_DEFINED__ */


/* header files for imported files */
#include "oaidl.h"
#include "ocidl.h"

#ifdef __cplusplus
extern "C"{
#endif 



#ifndef __FolderCryptoShellNativeLib_LIBRARY_DEFINED__
#define __FolderCryptoShellNativeLib_LIBRARY_DEFINED__

/* library FolderCryptoShellNativeLib */
/* [helpstring][version][uuid] */ 


EXTERN_C const IID LIBID_FolderCryptoShellNativeLib;

EXTERN_C const CLSID CLSID_COverlayHandler;

#ifdef __cplusplus

class DECLSPEC_UUID("F8A2C000-1234-4A5B-9C6D-7E8F9A0B1C2D")
COverlayHandler;
#endif

EXTERN_C const CLSID CLSID_CEncryptStateHandler;

#ifdef __cplusplus

class DECLSPEC_UUID("F8A2B000-1234-4A5B-9C6D-7E8F9A0B1C2D")
CEncryptStateHandler;
#endif

EXTERN_C const CLSID CLSID_CDecryptStateHandler;

#ifdef __cplusplus

class DECLSPEC_UUID("F8A2C100-1234-4A5B-9C6D-7E8F9A0B1C2D")
CDecryptStateHandler;
#endif
#endif /* __FolderCryptoShellNativeLib_LIBRARY_DEFINED__ */

/* Additional Prototypes for ALL interfaces */

/* end of Additional Prototypes */

#ifdef __cplusplus
}
#endif

#endif


