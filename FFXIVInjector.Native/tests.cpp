#include "loader.cpp"
#include <iostream>
static int originalCalls, localCalls;
static void* expectedManager = reinterpret_cast<void*>(0x1234);
static std::wstring expectedPath;
static unsigned char expectedSync;
static unsigned char __fastcall FakeOriginal(void* manager, unsigned char*, int priority, unsigned char sync) {
    if (manager != expectedManager || priority != 7 || sync != expectedSync) throw std::runtime_error("Original arguments changed");
    originalCalls++; return 9;
}
static unsigned char __fastcall FakeLocal(void* manager, unsigned char* desc, int priority, unsigned char sync) {
    if (manager != expectedManager || priority != 7 || sync != expectedSync || desc[0] != 0) throw std::runtime_error("Local ABI arguments");
    if (expectedPath != reinterpret_cast<wchar_t*>(desc + 0x70)) throw std::runtime_error("UTF16 path");
    auto fd = *reinterpret_cast<unsigned char**>(desc + 0x30);
    if (expectedPath != reinterpret_cast<wchar_t*>(fd + 0x21)) throw std::runtime_error("Secondary UTF16 path");
    auto handle = *reinterpret_cast<unsigned char**>(desc + 0x50);
    auto ptr = *reinterpret_cast<const char**>(handle + 0x48);
    auto len = *reinterpret_cast<size_t*>(handle + 0x58);
    if (std::string(ptr, len) != Narrow(expectedPath)) throw std::runtime_error("Loose reader resource filename");
    localCalls++; return 1;
}
static int textureOriginalCalls, textureLocalCalls, categoryCalls;
static unsigned char FakeTexture(unsigned char*, unsigned char*, unsigned char) { textureOriginalCalls++; return 0xf1; }
static unsigned char FakeLooseTexture(unsigned char*, int lod, unsigned char*, unsigned char failed) {
    if (lod != 0 || failed != 0) throw std::runtime_error("Loose texture arguments");
    textureLocalCalls++; return 1;
}
static void FakeCategory(unsigned char*) { categoryCalls++; }
static int soundOriginalCalls, soundLocalCalls;
static unsigned char FakeSound(unsigned char*, unsigned char*, unsigned char) { soundOriginalCalls++; return 9; }
static unsigned char FakeLooseSound(unsigned char*, unsigned char* desc, unsigned char) {
    if (desc[0] != 0) throw std::runtime_error("Sound loose mode");
    soundLocalCalls++; return 1;
}
int main() {
    try {
        if (!IsBannerPath("ui/icon/124000/fr/124567_hr1.tex") || !IsBannerPath("ui/icon/122000/ja/122007.tex")) throw std::runtime_error("All-zone banner scope");
        if (!IsBannerPath("ui/icon/120000/en/120111.tex") || !IsBannerPath("ui/icon/129000/129001_hr1.tex")) throw std::runtime_error("Full 12 icon scope");
        if (!IsBannerPath("ui/icon/180000/ja/180077.tex") || !IsBannerPath("ui/icon/181000/en/181173_hr1.tex")) throw std::runtime_error("18 icon scope");
        if (!IsBannerPath("ui/icon/061000/en/061035.tex") || !IsBannerPath("ui/icon/999000/999001.tex")) throw std::runtime_error("Manifest-driven icon scope");
        for (const auto* bad : {"ui/icon/124000/ko/124567.tex", "ui/icon/124000/en/123003.tex", "ui/icon/061000/en/061035.scd", "ui/icon/124000/en/124567.tex/../x"})
            if (IsBannerPath(bad)) throw std::runtime_error("Invalid banner accepted");
        originalRead = FakeOriginal; localRead = FakeLocal;
        originalTexture = FakeTexture; looseTexture = FakeLooseTexture; updateTextureCategory = FakeCategory;
        unsigned char texHandle[0x150] = {}, texDescriptor[0x278] = {};
        if (ReadTexture(texHandle, texDescriptor, 0) != 0xf1 || textureLocalCalls) throw std::runtime_error("Unrelated texture changed");
        activeLocalTexture = texHandle;
        if (ReadTexture(texHandle, texDescriptor, 0) != 1 || textureLocalCalls != 1 || categoryCalls != 1) throw std::runtime_error("Targeted loose texture decoder");
        texDescriptor[0] = 0xB;
        if (ReadTexture(texHandle, texDescriptor, 0) != 0xf1 || textureLocalCalls != 1) throw std::runtime_error("Packed texture must be unchanged");
        activeLocalTexture = nullptr;
        auto file = fs::temp_directory_path() / ("resource-swap-native-test-" + std::to_string(GetCurrentProcessId()) + ".tex");
        std::ofstream(file).write("test", 4); expectedPath = file.wstring();
        alignas(16) unsigned char descriptor[0x278] = {}, handle[0xB0] = {};
        descriptor[0] = 0xB;
        *reinterpret_cast<unsigned char**>(descriptor + 0x50) = handle;
        std::string target = "ui/icon/123000/en/123003.tex";
        *reinterpret_cast<const char**>(handle + 0x48) = target.c_str();
        *reinterpret_cast<size_t*>(handle + 0x58) = target.size();
        *reinterpret_cast<size_t*>(handle + 0x60) = 128;
        auto snapshot = std::vector<unsigned char>(descriptor, descriptor + sizeof(descriptor));
        if (Redirect(expectedManager, descriptor, 7, 0) != 9 || originalCalls != 1 || memcmp(snapshot.data(), descriptor, sizeof(descriptor))) throw std::runtime_error("Non-target must be unchanged");
        mappings.emplace(target, expectedPath);
        for (unsigned char sync : {0, 1}) {
            memcpy(descriptor, snapshot.data(), sizeof(descriptor)); expectedSync = sync;
            if (Redirect(expectedManager, descriptor, 7, sync) != 1 || descriptor[0] != 0) throw std::runtime_error("Target read mode");
            if (*reinterpret_cast<const char**>(handle + 0x48) != target.c_str()) throw std::runtime_error("Resource name/cache identity changed");
        }
        if (localCalls != 2 || originalCalls != 1) throw std::runtime_error("Routing count");
        originalSound = FakeSound; looseSound = FakeLooseSound;
        for (const auto* valid : {"sound/voice/vo_line/8205289_fr.scd", "cut/ex5/sound/voicem/a/b_fr.scd", "sound/battle/mon/test_fr.scd"})
            if (!IsVoicePath(valid)) throw std::runtime_error("French voice scope rejected");
        for (const auto* invalid : {"sound/voice/vo_line/1_de.scd", "cut/ex5/sound/a_de.scd", "sound/battle/mon/a_de.scd", "sound/voice/vo_line/1_en.scd", "sound/voice/vo_line/1_ja.scd", "sound/system/shared.scd", "music/ex5/a_de.scd", "cut/ex5/sound/../a_fr.scd"})
            if (IsVoicePath(invalid)) throw std::runtime_error("Unselected voice/shared/music accepted");
        for (const auto* language : {"fr"}) {
            std::string voice = std::string("sound/voice/vo_line/8205289_") + language + ".scd";
            mappings.emplace(voice, expectedPath);
            *reinterpret_cast<const char**>(handle + 0x48) = voice.c_str();
            *reinterpret_cast<size_t*>(handle + 0x58) = voice.size();
            for (unsigned char sync : {0, 1}) {
                memcpy(descriptor, snapshot.data(), sizeof(descriptor)); expectedSync = sync;
                if (Redirect(expectedManager, descriptor, 7, sync) != 1) throw std::runtime_error("Voice redirect ABI");
                if (activeLocalSound) throw std::runtime_error("Sound context leaked");
                // Completion after redirect context has been restored, including async reads.
                if (ReadSound(handle, descriptor, 0) != 1) throw std::runtime_error("Mapped sound decoder");
            }
        }
        if (soundLocalCalls != 2 || soundOriginalCalls) throw std::runtime_error("Sound routing count");
        std::string untouched = "sound/voice/vo_line/8205289_de.scd";
        *reinterpret_cast<const char**>(handle + 0x48) = untouched.c_str();
        *reinterpret_cast<size_t*>(handle + 0x58) = untouched.size();
        if (ReadSound(handle, descriptor, 0) != 9 || soundOriginalCalls != 1) throw std::runtime_error("German voice changed");
        *reinterpret_cast<const char**>(handle + 0x48) = target.c_str();
        *reinterpret_cast<size_t*>(handle + 0x58) = target.size();
        fs::remove(file);
        memcpy(descriptor, snapshot.data(), sizeof(descriptor)); expectedSync = 0;
        if (Redirect(expectedManager, descriptor, 7, 0) != 9 || memcmp(snapshot.data(), descriptor, sizeof(descriptor))) throw std::runtime_error("Missing local file fallback");
        descriptor[0] = 1;
        if (Redirect(expectedManager, descriptor, 7, 0) != 9) throw std::runtime_error("Other file mode");
        if (Redirect(expectedManager, nullptr, 7, 0) != 9) throw std::runtime_error("Null descriptor");
        if (Normalize("UI\\ICON\\123000\\EN\\123003.TEX") != target) throw std::runtime_error("Normalization");
        auto systemProc = SystemProc("DirectInput8Create");
        if (!systemProc) throw std::runtime_error("System DLL forwarding");
        auto proxy = LoadLibraryW(L"out/dinput8.dll");
        if (!proxy) throw std::runtime_error("Proxy DLL cannot load");
        for (const char* exportName : {"DirectInput8Create", "DllCanUnloadNow", "DllGetClassObject", "DllRegisterServer", "DllUnregisterServer", "GetdfDIJoystick"})
            if (!GetProcAddress(proxy, exportName)) throw std::runtime_error("Missing proxy export");
        const GUID input8w = {0xBF798031,0x483A,0x4DA2,{0xAA,0x99,0x5D,0x64,0xED,0x36,0x97,0x00}};
        void* input = nullptr;
        auto create = reinterpret_cast<decltype(&DirectInput8Create)>(GetProcAddress(proxy, "DirectInput8Create"));
        if (FAILED(create(GetModuleHandleW(nullptr), 0x800, input8w, &input, nullptr)) || !input) throw std::runtime_error("Proxy cannot create system input interface");
        static_cast<IUnknown*>(input)->Release();
        FreeLibrary(proxy);
        std::cout << "PASS: native routing, untouched non-targets, sync/async ABI, resource identity, missing-file fallback, mode/null guards, normalization, six proxy exports, actual system input interface forwarding.\n";
        return 0;
    } catch (const std::exception& ex) { std::cerr << ex.what() << "\n"; return 1; }
}
