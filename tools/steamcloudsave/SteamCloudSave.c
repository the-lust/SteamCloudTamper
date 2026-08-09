// SteamCloudSave.c - steam_api64 shim + RemoteStorage shadow lane for SCT.
//
// One DLL, three mounting styles (identical engine):
//   1) SHIM : rename this DLL to steam_api64.dll in a game folder (back up the real one).
//   2) GBE  : drop into <game>/steam_settings/load_dlls/SteamCloudSave.dll
//             (gbe_fork loads every DLL there via LoadLibraryW automatically).
//   3) OST  : load via OpenSteamTool's [inject] (library_x64/library_x86) into the game process.
//
// Config file steamcloudsave.cfg (or env SCT_SCS_CONFIG):
//   steamPath=D:\Steam        - where the REAL steam_api64.dll lives (passthrough)
//   shadowRoot=D:\sct_shadow  - shadow lane: all RemoteStorage I/O goes here
//   appid=91330               - the game being redirected
//   registryPath=<path>       - optional SCT registry.json (read-only info, logged)
//
// When appid + shadowRoot are set, ISteamRemoteStorage calls for that game read/write
// only "<shadowRoot>\<appid>\<file>" and never touch Steam's cloud or userdata.
// Otherwise everything forwards to the real steam_api64.dll next to steamPath.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <stdarg.h>

#ifdef __cplusplus
extern "C" {
#endif

static char        g_steamPath[MAX_PATH] = {0};
static char        g_shadowRoot[MAX_PATH] = {0};
static char        g_registryPath[MAX_PATH] = {0};
static uint32_t    g_targetAppId = 0;
static HMODULE     g_real = NULL;
static int         g_inited = 0;

static void sctLog(const char* fmt, ...)
{
    FILE* f = fopen("steamcloudsave.log", "a");
    if (!f) return;
    va_list ap; va_start(ap, fmt);
    vfprintf(f, fmt, ap); va_end(ap);
    fputs("\n", f);
    fclose(f);
}

static void loadConfigFrom(const char* cfgPath)
{
    FILE* f = fopen(cfgPath, "r");
    if (!f) { sctLog("sct: no config %s", cfgPath); return; }

    char line[1024];
    while (fgets(line, sizeof(line), f))
    {
        char key[256], val[768];
        if (sscanf(line, " %255[^=]=%767[^\r\n]", key, val) != 2) continue;
        if (!_stricmp(key, "steamPath"))      strncpy(g_steamPath, val, sizeof(g_steamPath) - 1);
        else if (!_stricmp(key, "shadowRoot")) strncpy(g_shadowRoot, val, sizeof(g_shadowRoot) - 1);
        else if (!_stricmp(key, "appid"))      g_targetAppId = (uint32_t)strtoul(val, NULL, 10);
        else if (!_stricmp(key, "registryPath")) strncpy(g_registryPath, val, sizeof(g_registryPath) - 1);
    }
    fclose(f);
    sctLog("sct: config %s steam=%s appid=%u shadow=%s registry=%s",
           cfgPath, g_steamPath, g_targetAppId, g_shadowRoot, g_registryPath);
}

static void loadConfig(void)
{
    if (g_inited) return;
    g_inited = 1;
    const char* cfgPath = getenv("SCT_SCS_CONFIG");
    if (cfgPath && cfgPath[0]) { loadConfigFrom(cfgPath); return; }
    const char* cfgEnv = getenv("SCT_HOOK_CONFIG");
    if (cfgEnv && cfgEnv[0]) { loadConfigFrom(cfgEnv); return; }
    loadConfigFrom("steamcloudsave.cfg");
}

static void mkdirs(const char* path)
{
    char tmp[MAX_PATH];
    strncpy(tmp, path, sizeof(tmp) - 1);
    for (char* p = tmp + 3; *p; p++)
    {
        if (*p == '\\') { *p = 0; CreateDirectoryA(tmp, NULL); *p = '\\'; }
    }
    CreateDirectoryA(tmp, NULL);
}

// shadow layout: <shadowRoot>\<appid>\<file>
static void shadowPathFor(const char* file, char* out, size_t outLen)
{
    snprintf(out, outLen, "%s\\%u\\%s", g_shadowRoot, g_targetAppId, file);
}

static HMODULE realModule(void)
{
    if (g_real) return g_real;
    if (!g_steamPath[0]) { sctLog("sct: no steamPath - not forwarding"); return NULL; }
    char dllPath[MAX_PATH * 2];
    snprintf(dllPath, sizeof(dllPath), "%s\\steam_api64.dll", g_steamPath);
    g_real = LoadLibraryA(dllPath);
    if (!g_real) sctLog("sct: load real %s failed %lu", dllPath, GetLastError());
    return g_real;
}

static FARPROC realFn(const char* name)
{
    HMODULE h = realModule();
    return h ? GetProcAddress(h, name) : NULL;
}

static int ShadowMode(void) { return g_targetAppId != 0 && g_shadowRoot[0] != 0; }

// ---------------------------------------------------------------------------
// ISteamRemoteStorage bridges (the exports a game's IAT references)
// ---------------------------------------------------------------------------
static int32_t WINAPI Hook_FileWrite(void* c, const char* file, const void* data, int32_t cb)
{
    if (ShadowMode())
    {
        char shadow[MAX_PATH * 2];
        shadowPathFor(file, shadow, sizeof(shadow));
        mkdirs(shadow);
        FILE* fp = fopen(shadow, "wb");
        if (!fp) { sctLog("sct: shadow write open fail %s", shadow); return 0; }
        fwrite(data, 1, cb, fp); fclose(fp);
        sctLog("sct: shadow write %s (%d)", shadow, cb);
        return cb;
    }
    typedef int32_t (*F)(void*, const char*, const void*, int32_t);
    F f = (F)realFn("SteamAPI_ISteamRemoteStorage_FileWrite");
    return f ? f(c, file, data, cb) : 0;
}

static int32_t WINAPI SctFileRead(void* c, const char* file, void* data, int32_t toRead)
{
    if (ShadowMode())
    {
        char shadow[MAX_PATH * 2];
        shadowPathFor(file, shadow, sizeof(shadow));
        FILE* fp = fopen(shadow, "rb");
        if (fp) { size_t got = fread(data, 1, toRead, fp); fclose(fp); return (int32_t)got; }
        sctLog("sct: shadow read miss %s", file);
        return 0;
    }
    typedef int32_t (*FN)(void*, const char*, void*, int32_t);
    FN f = (FN) realFn("SteamAPI_ISteamRemoteStorage_FileRead");
    return f ? f(c, file, data, toRead) : 0;
}

static bool WINAPI SctFileDelete(void* c, const char* file)
{
    if (ShadowMode())
    {
        char shadow[MAX_PATH * 2];
        shadowPathFor(file, shadow, sizeof(shadow));
        DeleteFileA(shadow);
        return true;
    }
    typedef bool (*FN)(void*, const char*);
    FN f = (FN) realFn("SteamAPI_ISteamRemoteStorage_FileDelete");
    return f ? f(c, file) : false;
}

static bool WINAPI SctFileForget(void* c, const char* file)
{
    if (ShadowMode()) return true;
    typedef bool (*FN)(void*, const char*);
    FN f = (FN) realFn("SteamAPI_ISteamRemoteStorage_FileForget");
    return f ? f(c, file) : false;
}

static bool WINAPI SctFileExists(void* c, const char* file)
{
    if (ShadowMode())
    {
        char shadow[MAX_PATH];
        shadowPathFor(file, shadow, sizeof(shadow));
        return GetFileAttributesA(shadow) != INVALID_FILE_ATTRIBUTES;
    }
    typedef bool (*FN)(void*, const char*);
    FN f = (FN) realFn("SteamAPI_ISteamRemoteStorage_FileExists");
    return f ? f(c, file) : false;
}

static int64_t WINAPI SctFileSize(void* c, const char* file)
{
    if (ShadowMode())
    {
        char shadow[MAX_PATH];
        shadowPathFor(file, shadow, sizeof(shadow));
        HANDLE h = CreateFileA(shadow, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
        if (h == INVALID_HANDLE_VALUE) return 0;
        LARGE_INTEGER sz;
        GetFileSizeEx(h, &sz);
        CloseHandle(h);
        return sz.QuadPart;
    }
    typedef int64_t (*FN)(void*, const char*);
    FN f = (FN) realFn("SteamAPI_ISteamRemoteStorage_FileSize");
    return f ? f(c, file) : 0;
}

static bool WINAPI SctFilePersisted(void* c, const char* file)
{
    if (ShadowMode()) return true;
    typedef bool (*FN)(void*, const char*);
    FN f = (FN) realFn("SteamAPI_ISteamRemoteStorage_FilePersisted");
    return f ? f(c, file) : false;
}

static int64_t WINAPI SctFileTimestamp(void* c, const char* file)
{
    if (ShadowMode())
    {
        char shadow[MAX_PATH];
        shadowPathFor(file, shadow, sizeof(shadow));
        HANDLE h = CreateFileA(shadow, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
        if (h == INVALID_HANDLE_VALUE) return 0;
        FILETIME ft;
        GetFileTime(h, NULL, NULL, &ft);
        CloseHandle(h);
        uint64_t t = ((uint64_t)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
        return (int64_t)(t / 10000000ULL - 11644473600ULL);
    }
    typedef int64_t (*FN)(void*, const char*);
    FN f = (FN) realFn("SteamAPI_ISteamRemoteStorage_FileGetTimestamp");
    return f ? f(c, file) : 0;
}

// Generic 1:1 forward (arbitrary signature) used by exports not shadow-managed.
#define PASSTHRU(ret, name, expsig, call) \
    __declspec(dllexport) ret WINAPI name expsig \
    { \
        typedef ret (*FN) expsig; \
        FN _fn = (FN) realFn(#name); \
        return _fn ? _fn call : (ret)0; \
    }

PASSTHRU(int32_t, SteamAPI_ISteamRemoteStorage_GetFileCount, (void* c), (c))
PASSTHRU(bool, SteamAPI_ISteamRemoteStorage_IsCloudEnabledForApp, (void* c), (c))
PASSTHRU(bool, SteamAPI_ISteamRemoteStorage_IsCloudEnabledForAccount, (void* c), (c))
PASSTHRU(void, SteamAPI_ISteamRemoteStorage_SetCloudEnabledForApp, (void* c, bool b), (c, b))
PASSTHRU(int32_t, SteamAPI_ISteamRemoteStorage_FileReadAsync, (void* c, const char* f, uint32_t o, uint32_t n), (c, f, o, n))
PASSTHRU(bool, SteamAPI_ISteamRemoteStorage_FileReadAsyncComplete, (void* c, uint64_t call, void* b, uint32_t n), (c, call, b, n))

// Exports for the shadow-managed set:
#define SHADOW_EXPORT(ret, name, expsig, impl) \
    __declspec(dllexport) ret WINAPI name expsig { return impl; }

SHADOW_EXPORT(int32_t, SteamRemoteStorage_FileWrite, (void* c, const char* f, const void* d, int32_t n), Hook_FileWrite(c, f, d, n))
SHADOW_EXPORT(int32_t, SteamRemoteStorage_FileRead, (void* c, const char* f, void* d, int32_t n), SctFileRead(c, f, d, n))
SHADOW_EXPORT(bool, SteamRemoteStorage_FileDelete, (void* c, const char* f), SctFileDelete(c, f))
SHADOW_EXPORT(bool, SteamRemoteStorage_FileForget, (void* c, const char* f), SctFileForget(c, f))
SHADOW_EXPORT(bool, SteamRemoteStorage_FileExists, (void* c, const char* f), SctFileExists(c, f))
SHADOW_EXPORT(int64_t, SteamRemoteStorage_FileSize, (void* c, const char* f), SctFileSize(c, f))
SHADOW_EXPORT(bool, SteamRemoteStorage_FilePersisted, (void* c, const char* f), SctFilePersisted(c, f))
SHADOW_EXPORT(int64_t, SteamRemoteStorage_FileGetTimestamp, (void* c, const char* f), SctFileTimestamp(c, f))

// The real steam_api64 export table uses "SteamAPI_ISteamRemoteStorage_*" names;
// expose the official names too so games linking by them reach us first.
__declspec(dllexport) int32_t WINAPI SteamAPI_ISteamRemoteStorage_FileWrite(void* c, const char* f, const void* d, int32_t n)
{ return Hook_FileWrite(c, f, d, n); }
__declspec(dllexport) int32_t WINAPI SteamAPI_ISteamRemoteStorage_FileRead(void* c, const char* f, void* d, int32_t n)
{ return SctFileRead(c, f, d, n); }
__declspec(dllexport) bool WINAPI SteamAPI_ISteamRemoteStorage_FileDelete(void* c, const char* f)
{ return SctFileDelete(c, f); }
__declspec(dllexport) bool WINAPI SteamAPI_ISteamRemoteStorage_FileForget(void* c, const char* f)
{ return SctFileForget(c, f); }
__declspec(dllexport) bool WINAPI SteamAPI_ISteamRemoteStorage_FileExists(void* c, const char* f)
{ return SctFileExists(c, f); }
__declspec(dllexport) int64_t WINAPI SteamAPI_ISteamRemoteStorage_FileSize(void* c, const char* f)
{ return SctFileSize(c, f); }
__declspec(dllexport) bool WINAPI SteamAPI_ISteamRemoteStorage_FilePersisted(void* c, const char* f)
{ return SctFilePersisted(c, f); }
__declspec(dllexport) int64_t WINAPI SteamAPI_ISteamRemoteStorage_FileGetTimestamp(void* c, const char* f)
{ return SctFileTimestamp(c, f); }

// Steam API core entrypoints a game's import table may need:
typedef bool (*InitFn)();
__declspec(dllexport) bool WINAPI SteamAPI_Init(void)
{
    HMODULE h = realModule();
    if (!h) return false;
    InitFn fn = (InitFn)GetProcAddress(h, "SteamAPI_Init");
    return fn ? fn() : false;
}
__declspec(dllexport) void WINAPI SteamAPI_Shutdown(void)
{
    HMODULE h = realModule();
    if (!h) return;
    typedef void (*FN)();
    FN fn = (FN)GetProcAddress(h, "SteamAPI_Shutdown");
    if (fn) fn();
}
__declspec(dllexport) void WINAPI SteamAPI_RunCallbacks(void)
{
    HMODULE h = realModule();
    if (!h) return;
    typedef void (*FN)();
    FN fn = (FN)GetProcAddress(h, "SteamAPI_RunCallbacks");
    if (fn) fn();
}
__declspec(dllexport) uint64_t WINAPI SteamAPI_GetHSteamUser(void)
{
    HMODULE h = realModule();
    if (!h) return 0;
    typedef uint64_t (*FN)();
    FN fn = (FN)GetProcAddress(h, "SteamAPI_GetHSteamUser");
    return fn ? fn() : 0;
}
__declspec(dllexport) uint64_t WINAPI SteamAPI_GetHSteamPipe(void)
{
    HMODULE h = realModule();
    if (!h) return 0;
    typedef uint64_t (*FN)();
    FN fn = (FN)GetProcAddress(h, "SteamAPI_GetHSteamPipe");
    return fn ? fn() : 0;
}

// ---------------------------------------------------------------------------
// Public SteamCloudSave API (the "one brain" entry points for other tools)
// ---------------------------------------------------------------------------
__declspec(dllexport) int WINAPI SteamCloudSave_Init(const char* configPath)
{
    loadConfig();
    if (configPath && configPath[0]) loadConfigFrom(configPath);
    if (g_registryPath[0])
    {
        DWORD attrs = GetFileAttributesA(g_registryPath);
        sctLog("sct: registry %s %s", g_registryPath,
               attrs != INVALID_FILE_ATTRIBUTES ? "present" : "missing");
    }
    return 1;
}

__declspec(dllexport) void WINAPI SteamCloudSave_Shutdown(void)
{
    if (g_real) { FreeLibrary(g_real); g_real = NULL; }
    sctLog("sct: shutdown");
}

__declspec(dllexport) const char* WINAPI SteamCloudSave_State(void)
{
    return ShadowMode() ? "redirecting" : "off";
}

__declspec(dllexport) uint32_t WINAPI SteamCloudSave_App(void)
{
    return g_targetAppId;
}

__declspec(dllexport) const char* WINAPI SteamCloudSave_ShadowRoot(void)
{
    return g_shadowRoot;
}

BOOL WINAPI DllMain(HINSTANCE hInst, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) loadConfig();
    return TRUE;
}

#ifdef __cplusplus
}
#endif