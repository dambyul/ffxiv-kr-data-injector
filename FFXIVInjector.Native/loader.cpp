#define WIN32_LEAN_AND_MEAN
#define DIRECTINPUT_VERSION 0x0800
#include <windows.h>
#include <bcrypt.h>
#include <dinput.h>
#include <filesystem>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>
#include <unordered_map>
#include <mutex>
#include <algorithm>
#include <cctype>
#include <cstring>
#include <regex>
#include "vendor/minhook-1.3.4/include/MinHook.h"

namespace fs = std::filesystem;
using Reader = unsigned char(__fastcall*)(void*, unsigned char*, int, unsigned char);
static HMODULE module;
static Reader originalRead, localRead;
using TextureReader = unsigned char(__fastcall*)(unsigned char*, unsigned char*, unsigned char);
using LooseTextureReader = unsigned char(__fastcall*)(unsigned char*, int, unsigned char*, unsigned char);
using TextureCategory = void(__fastcall*)(unsigned char*);
static TextureReader originalTexture;
static LooseTextureReader looseTexture;
static TextureCategory updateTextureCategory;
using SoundReader = unsigned char(__fastcall*)(unsigned char*, unsigned char*, unsigned char);
static SoundReader originalSound, looseSound;
static thread_local unsigned char* activeLocalSound;
static thread_local unsigned char* activeLocalTexture;
static unsigned char __fastcall ReadTexture(unsigned char* handle, unsigned char* descriptor, unsigned char failedOpen) {
    auto result = originalTexture(handle, descriptor, failedOpen);
    if (handle != activeLocalTexture || !handle || descriptor[0] != 0) return result;
    if (result == 1) return result;
    // Targeted loose TEX needs the local decoder after the RSF lookup fails.
    // These banner files contain one mip: select LOD 0.
    auto localResult = looseTexture(handle, 0, descriptor, failedOpen);
    updateTextureCategory(handle);
    return localResult;
}
static std::unordered_map<std::string, std::wstring> mappings;
static std::wstring logPath;
static std::mutex logMutex;
static std::once_flag initializeFlag, systemFlag;
static HMODULE systemInput;

static void Log(const std::string& message) {
    if (logPath.empty()) return;
    std::lock_guard<std::mutex> lock(logMutex);
    std::ofstream stream(fs::path(logPath), std::ios::app);
    stream << GetTickCount64() << " " << message << "\n";
}
static std::string Hash(const fs::path& path) {
    std::ifstream in(path, std::ios::binary);
    if (!in) return {};
    BCRYPT_ALG_HANDLE alg = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) return {};
    if (BCryptCreateHash(alg, &hash, nullptr, 0, nullptr, 0, 0) < 0) { BCryptCloseAlgorithmProvider(alg, 0); return {}; }
    unsigned char buffer[65536], digest[32];
    bool valid = true;
    while (in) {
        in.read(reinterpret_cast<char*>(buffer), sizeof(buffer));
        if (in.gcount() && BCryptHashData(hash, buffer, static_cast<ULONG>(in.gcount()), 0) < 0) { valid = false; break; }
    }
    valid = valid && !in.bad() && BCryptFinishHash(hash, digest, sizeof(digest), 0) >= 0;
    BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(alg, 0);
    if (!valid) return {};
    std::string out;
    for (auto b : digest) { out += "0123456789abcdef"[b >> 4]; out += "0123456789abcdef"[b & 15]; }
    return out;
}
static std::wstring Wide(const std::string& value) {
    int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (!length) return {};
    std::wstring out(length, 0);
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), out.data(), length);
    return out;
}
static std::string Narrow(const std::wstring& value) {
    int length = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    std::string out(length, 0);
    WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), out.data(), length, nullptr, nullptr);
    return out;
}
static std::string Normalize(std::string path) {
    for (auto& c : path) { if (c == '\\') c = '/'; c = static_cast<char>(std::tolower(static_cast<unsigned char>(c))); }
    return path;
}
static bool IsBannerPath(const std::string& path) {
    static const std::regex pattern("^ui/icon/([0-9]{3})000/((en|ja|de|fr)/)?([0-9]{3})[0-9]{3}(_hr1)?\\.tex$");
    std::smatch match;
    return std::regex_match(path, match, pattern) && match[1] == match[4];
}
static bool IsVoicePath(const std::string& path) {
    static const std::regex pattern("^(cut/(ffxiv|ex[0-9]+)/sound/|sound/(voice/|battle/mon/))[a-z0-9_/]+_fr\\.scd$");
    return std::regex_match(path, pattern) && path.find("..") == std::string::npos;
}
// Small SEH helper: inspect foreign memory without C++ objects/destructors in this frame.
static size_t ReadName(unsigned char* descriptor, char* output, size_t maximum, unsigned char** handle) {
    __try {
        if (!descriptor || descriptor[0] != 0xB) return 0;
        *handle = *reinterpret_cast<unsigned char**>(descriptor + 0x50);
        if (!*handle) return 0;
        auto length = *reinterpret_cast<size_t*>(*handle + 0x58);
        auto capacity = *reinterpret_cast<size_t*>(*handle + 0x60);
        if (!length || length >= maximum || capacity < length || capacity > 0x100000) return 0;
        auto name = capacity < 16 ? reinterpret_cast<char*>(*handle + 0x48) : *reinterpret_cast<char**>(*handle + 0x48);
        if (!name) return 0;
        memcpy(output, name, length); output[length] = 0;
        return length;
    } __except(EXCEPTION_EXECUTE_HANDLER) { return 0; }
}
static size_t ReadHandleName(unsigned char* handle, char* output, size_t maximum) {
    __try {
        if (!handle) return 0;
        auto length = *reinterpret_cast<size_t*>(handle + 0x58);
        auto capacity = *reinterpret_cast<size_t*>(handle + 0x60);
        if (!length || length >= maximum || capacity < length || capacity > 0x100000) return 0;
        auto name = capacity < 16 ? reinterpret_cast<char*>(handle + 0x48) : *reinterpret_cast<char**>(handle + 0x48);
        if (!name) return 0;
        memcpy(output, name, length); output[length] = 0; return length;
    } __except(EXCEPTION_EXECUTE_HANDLER) { return 0; }
}
static unsigned char __fastcall ReadSound(unsigned char* handle, unsigned char* descriptor, unsigned char failedOpen) {
    if (handle && descriptor && descriptor[0] == 0) {
        char name[512]; auto length = ReadHandleName(handle, name, sizeof(name));
        // The original resource name is restored after scheduling. Check it again
        // on async completion, where the thread-local redirect context is gone.
        auto key = length ? Normalize(std::string(name, length)) : std::string();
        if (handle == activeLocalSound || (IsVoicePath(key) && mappings.contains(key)))
            return looseSound(handle, descriptor, failedOpen);
    }
    return originalSound(handle, descriptor, failedOpen);
}
static unsigned char __fastcall Redirect(void* manager, unsigned char* descriptor, int priority, unsigned char sync) {
    char name[256]; unsigned char* handle = nullptr;
    auto length = ReadName(descriptor, name, sizeof(name), &handle);
    if (!length) return originalRead(manager, descriptor, priority, sync);
    auto key = Normalize(std::string(name, length));
    auto entry = mappings.find(key);
    if (entry == mappings.end()) return originalRead(manager, descriptor, priority, sync);
    const auto& path = entry->second;
    if (path.size() >= 260 || GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES) {
        Log("FALLBACK missing local file: " + key);
        return originalRead(manager, descriptor, priority, sync);
    }
    // The loose resource loader also receives the absolute UTF8 filename through
    // ResourceHandle. Preserve the std::string identity after the reader returns.
    auto utf8Path = Narrow(path);
    unsigned char originalName[32];
    memcpy(originalName, handle + 0x48, sizeof(originalName));
    *reinterpret_cast<const char**>(handle + 0x48) = utf8Path.c_str();
    *reinterpret_cast<size_t*>(handle + 0x58) = utf8Path.size();
    *reinterpret_cast<size_t*>(handle + 0x60) = utf8Path.size();
    Log("READ_INPUT " + key + " length=" + std::to_string(*reinterpret_cast<unsigned long long*>(descriptor + 0x10))
        + " offset=" + std::to_string(*reinterpret_cast<unsigned long long*>(descriptor + 0x18)));
    // Match the game's loose-file reader contract.
    // The native file reader consumes the inline UTF16 path and the path at FileInterface+0x21.
    wchar_t pathDescriptor[0x11 + 260] = {};
    memcpy(pathDescriptor + 0x11, path.c_str(), (path.size() + 1) * sizeof(wchar_t));
    descriptor[0] = 0; // LoadUnpackedResource
    *reinterpret_cast<void**>(descriptor + 0x30) = reinterpret_cast<unsigned char*>(pathDescriptor) + 1;
    memcpy(descriptor + 0x70, path.c_str(), (path.size() + 1) * sizeof(wchar_t));
    Log("SWAP " + key + " sync=" + std::to_string(sync));
    auto previousTexture = activeLocalTexture;
    auto previousSound = activeLocalSound;
    if (IsVoicePath(key)) activeLocalSound = handle;
    else activeLocalTexture = handle;
    auto result = localRead(manager, descriptor, priority, sync);
    activeLocalTexture = previousTexture;
    activeLocalSound = previousSound;
    memcpy(handle + 0x48, originalName, sizeof(originalName));
    Log("READ_STATE " + key + " io=" + std::to_string(handle[0x68])
        + " read=" + std::to_string(handle[0xA8]) + " load=" + std::to_string(handle[0xA9])
        + " size=" + std::to_string(*reinterpret_cast<unsigned int*>(handle + 0x28))
        + " total=" + std::to_string(*reinterpret_cast<unsigned int*>(handle + 0x2C)));
    // Keep LoadUnpackedResource mode for later completion, as in the reference loader.
    // ReadFile consumes/copies the stack path descriptor during this call.
    Log("READ_RETURN " + key + " value=" + std::to_string(result));
    // The result is scheduling status, not proof of successful texture rendering. Do not retry an already queued read.
    return result;
}
static unsigned char* Scan(HMODULE image, const std::string& signature, int& hits) {
    std::istringstream tokens(signature); std::string token; std::vector<int> pattern;
    while (tokens >> token) pattern.push_back(token == "??" ? -1 : std::stoi(token, nullptr, 16));
    auto base = reinterpret_cast<unsigned char*>(image);
    auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    auto nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    auto section = IMAGE_FIRST_SECTION(nt); unsigned char* found = nullptr;
    for (int s = 0; s < nt->FileHeader.NumberOfSections; s++, section++) {
        if (memcmp(section->Name, ".text", 5)) continue;
        auto text = base + section->VirtualAddress; auto size = section->Misc.VirtualSize;
        for (size_t i = 0; i + pattern.size() <= size; i++) {
            if (text[i] != pattern[0]) continue;
            bool equal = true;
            for (size_t j = 1; j < pattern.size(); j++) if (pattern[j] >= 0 && text[i+j] != pattern[j]) { equal = false; break; }
            if (equal) { hits++; found = text + i; }
        }
    }
    return found;
}
static void Initialize() {
    try {
        wchar_t dllName[32768], exeName[32768];
        GetModuleFileNameW(module, dllName, 32768); GetModuleFileNameW(nullptr, exeName, 32768);
        if (fs::path(exeName).filename() != L"ffxiv_dx11.exe") return;
        std::ifstream config(fs::path(dllName).parent_path() / "resource-swap.ini");
        std::string mapFile, expectedExeHash; std::getline(config, mapFile); std::getline(config, expectedExeHash);
        auto mapPath = fs::path(Wide(mapFile));
        logPath = (mapPath.parent_path() / "native-loader.log").wstring();
        Log("START selective native loader");
        if (!config || expectedExeHash.size() != 64) { Log("DISABLED invalid loader configuration"); return; }
        if (Hash(exeName) != expectedExeHash)
            Log("WARNING executable hash changed; continuing with signature validation");
        std::ifstream map(mapPath); std::string line;
        while (std::getline(map, line)) {
            std::istringstream fields(line); std::string target, file, sha;
            std::getline(fields, target, '\t'); std::getline(fields, file, '\t'); std::getline(fields, sha);
            if (!sha.empty() && sha.back() == '\r') sha.pop_back();
            auto path = Wide(file); auto normalized = Normalize(target);
            if (!IsBannerPath(normalized) && !IsVoicePath(normalized)) {
                Log("DISABLED invalid scope"); return;
            }
            if (!fs::path(path).is_absolute() || path.size() >= 260 || sha.size() != 64) { Log("DISABLED invalid mapping"); return; }
            // Payload headers were checked during extraction; do not rescan all assets at startup.
            if (!mappings.emplace(normalized, path).second) { mappings.clear(); Log("DISABLED duplicate mapping"); return; }
        }
        if (!map.eof() || mappings.empty() || mappings.size() > 100000) { mappings.clear(); Log("DISABLED map read/scope error"); return; }
        bool hasTextures = std::any_of(mappings.begin(), mappings.end(), [](const auto& e) { return IsBannerPath(e.first); });
        bool hasVoices = std::any_of(mappings.begin(), mappings.end(), [](const auto& e) { return IsVoicePath(e.first); });
        int soundHits = 0, soundLocalHits = 0;
        int sqHits = 0, fileHits = 0, texHits = 0, looseHits = 0, categoryHits = 0; auto image = GetModuleHandleW(nullptr);
        auto sq = Scan(image, "40 56 41 56 48 83 EC ?? 0F BE 02", sqHits);
        auto file = Scan(image, "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 55 41 56 41 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 63 42", fileHits);
        auto tex = Scan(image, "40 53 55 41 54 41 55 41 56 41 57 48 81 EC ?? ?? ?? ?? 48 8B D9", texHits);
        auto loose = Scan(image, "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 48 89 7C 24 ?? 41 56 48 83 EC ?? 49 8B E8 44 88 4C 24", looseHits);
        auto category = Scan(image, "0F B7 41 ?? FF C8", categoryHits);
        if (hasVoices) {
            auto sound = Scan(image, "40 56 57 41 54 48 81 EC ?? ?? ?? ?? 80 3A ?? 45 0F B6 E0 48 8B F2 48 8B F9 75 ?? 83 BA ?? ?? ?? ?? ?? 72 ?? 48 8B 01 FF 90 ?? ?? ?? ?? 3C", soundHits);
            auto soundLocal = Scan(image, "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 30 8B 79 ?? 48 8B DA 8B D7", soundLocalHits);
            originalSound = reinterpret_cast<SoundReader>(sound); looseSound = reinterpret_cast<SoundReader>(soundLocal);
        }
        if (sqHits != 1 || fileHits != 1) { Log("DISABLED common reader signature count"); return; }
        if (hasTextures && (texHits != 1 || looseHits != 1 || categoryHits != 1)) {
            std::erase_if(mappings, [](const auto& entry) { return IsBannerPath(entry.first); });
            hasTextures = false; Log("IMAGES_DISABLED texture signatures changed; voice scope retained");
        }
        if (hasVoices && (soundHits != 1 || soundLocalHits != 1)) {
            std::erase_if(mappings, [](const auto& entry) { return IsVoicePath(entry.first); });
            hasVoices = false; Log("VOICES_DISABLED sound signatures changed; image scope retained");
        }
        if (mappings.empty()) { Log("DISABLED no compatible reader hooks"); return; }
        localRead = reinterpret_cast<Reader>(file);
        looseTexture = reinterpret_cast<LooseTextureReader>(loose);
        updateTextureCategory = reinterpret_cast<TextureCategory>(category);
        auto status = MH_Initialize();
        if (status != MH_OK) { Log("DISABLED MinHook initialization=" + std::to_string(status)); return; }
        status = MH_CreateHook(sq, Redirect, reinterpret_cast<void**>(&originalRead));
        if (status != MH_OK) { Log("DISABLED hook creation=" + std::to_string(status)); return; }
        if (hasTextures) {
            status = MH_CreateHook(tex, ReadTexture, reinterpret_cast<void**>(&originalTexture));
            if (status != MH_OK) { MH_RemoveHook(sq); Log("DISABLED texture hook creation"); return; }
        }
        if (hasVoices) {
            auto sound = reinterpret_cast<void*>(originalSound);
            status = MH_CreateHook(sound, ReadSound, reinterpret_cast<void**>(&originalSound));
            if (status != MH_OK) { if (hasTextures) MH_RemoveHook(tex); MH_RemoveHook(sq); Log("DISABLED sound hook creation"); return; }
            status = MH_EnableHook(sound);
            if (status != MH_OK) { MH_RemoveHook(sound); if (hasTextures) MH_RemoveHook(tex); MH_RemoveHook(sq); Log("DISABLED sound hook enable"); return; }
        }
        if (hasTextures) {
            status = MH_EnableHook(tex);
            if (status != MH_OK) { MH_DisableHook(MH_ALL_HOOKS); MH_RemoveHook(MH_ALL_HOOKS); Log("DISABLED texture hook enable"); return; }
        }
        status = MH_EnableHook(sq);
        if (status != MH_OK) { MH_DisableHook(MH_ALL_HOOKS); MH_RemoveHook(MH_ALL_HOOKS); Log("DISABLED hook enable"); return; }
        Log("READY mappings=" + std::to_string(mappings.size()) + "; other resource reads unchanged");
    } catch (const std::exception& ex) { Log(std::string("DISABLED exception: ") + ex.what()); }
}
static FARPROC SystemProc(const char* name) {
    std::call_once(systemFlag, [] {
        wchar_t systemPath[MAX_PATH]; GetSystemDirectoryW(systemPath, MAX_PATH);
        systemInput = LoadLibraryW((fs::path(systemPath) / "dinput8.dll").c_str());
    });
    return systemInput ? GetProcAddress(systemInput, name) : nullptr;
}
extern "C" HRESULT WINAPI DirectInput8Create(HINSTANCE h, DWORD version, REFIID iid, LPVOID* output, LPUNKNOWN outer) {
    std::call_once(initializeFlag, Initialize);
    auto fn = reinterpret_cast<decltype(&DirectInput8Create)>(SystemProc("DirectInput8Create"));
    return fn ? fn(h, version, iid, output, outer) : E_FAIL;
}
extern "C" HRESULT WINAPI DllCanUnloadNow() { auto fn = reinterpret_cast<HRESULT(WINAPI*)()>(SystemProc("DllCanUnloadNow")); return fn ? fn() : S_FALSE; }
extern "C" HRESULT WINAPI DllGetClassObject(REFCLSID cls, REFIID iid, void** out) { auto fn = reinterpret_cast<HRESULT(WINAPI*)(REFCLSID, REFIID, void**)>(SystemProc("DllGetClassObject")); return fn ? fn(cls, iid, out) : E_FAIL; }
extern "C" HRESULT WINAPI DllRegisterServer() { auto fn = reinterpret_cast<HRESULT(WINAPI*)()>(SystemProc("DllRegisterServer")); return fn ? fn() : E_FAIL; }
extern "C" HRESULT WINAPI DllUnregisterServer() { auto fn = reinterpret_cast<HRESULT(WINAPI*)()>(SystemProc("DllUnregisterServer")); return fn ? fn() : E_FAIL; }
extern "C" LPCDIDATAFORMAT WINAPI GetdfDIJoystick() { auto fn = reinterpret_cast<LPCDIDATAFORMAT(WINAPI*)()>(SystemProc("GetdfDIJoystick")); return fn ? fn() : nullptr; }
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) { if (reason == DLL_PROCESS_ATTACH) { module = instance; DisableThreadLibraryCalls(instance); } return TRUE; }
