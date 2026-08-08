#include <windows.h>
#include <shobjidl.h>
#include <shlguid.h>
#include <shlwapi.h>
#include <wrl/client.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <filesystem>
#include <new>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace
{
constexpr wchar_t RegistryPath[] = L"Software\\ClipPort\\ExplorerContextMenu";
constexpr wchar_t ApplicationName[] = L"ClipPort.exe";
constexpr CLSID RootCommandClsid = {0x2a506163, 0xa3a8, 0x4a6f, {0x9f, 0x5b, 0x53, 0xd7, 0x68, 0xe5, 0xe2, 0x0c}};
constexpr GUID SourceCommandGuid = {0x524542fa, 0x27bf, 0x40d4, {0xbc, 0x51, 0xb3, 0x2d, 0xdd, 0x45, 0xe9, 0x11}};
constexpr GUID DestinationCommandGuid = {0xdfb92535, 0x216d, 0x42ee, {0xa3, 0x27, 0x5d, 0x99, 0x66, 0xe1, 0xc0, 0x74}};
std::atomic_long OutstandingObjects = 0;

enum class CommandKind { Root, Source, Destination };

struct MenuConfiguration
{
    bool Enabled = false;
    std::wstring Language = L"zh-CN";
    std::wstring InstallDirectory;
};

std::wstring ReadRegistryString(HKEY key, const wchar_t* name)
{
    DWORD type = 0;
    DWORD bytes = 0;
    if (RegQueryValueExW(key, name, nullptr, &type, nullptr, &bytes) != ERROR_SUCCESS ||
        (type != REG_SZ && type != REG_EXPAND_SZ) || bytes < sizeof(wchar_t))
    {
        return {};
    }
    std::vector<wchar_t> buffer(bytes / sizeof(wchar_t) + 1, L'\0');
    if (RegQueryValueExW(key, name, nullptr, &type,
        reinterpret_cast<BYTE*>(buffer.data()), &bytes) != ERROR_SUCCESS)
    {
        return {};
    }
    return buffer.data();
}

MenuConfiguration ReadConfiguration()
{
    MenuConfiguration configuration;
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, RegistryPath, 0, KEY_QUERY_VALUE, &key) != ERROR_SUCCESS)
    {
        return configuration;
    }
    DWORD enabled = 0;
    DWORD type = 0;
    DWORD bytes = sizeof(enabled);
    if (RegQueryValueExW(key, L"Enabled", nullptr, &type,
        reinterpret_cast<BYTE*>(&enabled), &bytes) == ERROR_SUCCESS && type == REG_DWORD)
    {
        configuration.Enabled = enabled == 1;
    }
    if (std::wstring language = ReadRegistryString(key, L"Language"); !language.empty())
    {
        configuration.Language = std::move(language);
    }
    configuration.InstallDirectory = ReadRegistryString(key, L"InstallDirectory");
    RegCloseKey(key);
    return configuration;
}

const wchar_t* LocalizedTitle(CommandKind kind, const std::wstring& language)
{
    const bool english = _wcsicmp(language.c_str(), L"en-US") == 0;
    const bool classical = _wcsicmp(language.c_str(), L"lzh") == 0;
    switch (kind)
    {
    case CommandKind::Root:
        return english ? L"New ClipPort task" : classical ? L"立 ClipPort 之役" : L"新建 ClipPort 任务";
    case CommandKind::Source:
        return english ? L"Use as source directory" : classical ? L"以为源目录" : L"作为源目录";
    case CommandKind::Destination:
        return english ? L"Use as destination directory" : classical ? L"以为所往目录" : L"作为目标目录";
    }
    return L"ClipPort";
}

bool IsDirectory(const std::wstring& path)
{
    const DWORD attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
}

bool TryGetPathFromItem(IShellItem* item, std::wstring& path)
{
    PWSTR rawPath = nullptr;
    if (item == nullptr || FAILED(item->GetDisplayName(SIGDN_FILESYSPATH, &rawPath)))
    {
        return false;
    }
    path.assign(rawPath);
    CoTaskMemFree(rawPath);
    return IsDirectory(path);
}

bool TryGetSelectedDirectory(IShellItemArray* items, std::wstring& path)
{
    DWORD count = 0;
    if (items == nullptr || FAILED(items->GetCount(&count)) || count != 1)
    {
        return false;
    }
    ComPtr<IShellItem> item;
    return SUCCEEDED(items->GetItemAt(0, &item)) && TryGetPathFromItem(item.Get(), path);
}

bool TryGetBackgroundDirectory(IUnknown* site, std::wstring& path)
{
    ComPtr<IServiceProvider> serviceProvider;
    ComPtr<IFolderView> folderView;
    ComPtr<IShellItem> folder;
    return site != nullptr && SUCCEEDED(site->QueryInterface(IID_PPV_ARGS(&serviceProvider))) &&
        SUCCEEDED(serviceProvider->QueryService(SID_SFolderView, IID_PPV_ARGS(&folderView))) &&
        SUCCEEDED(folderView->GetFolder(IID_PPV_ARGS(&folder))) &&
        TryGetPathFromItem(folder.Get(), path);
}

std::wstring QuoteArgument(const std::wstring& argument)
{
    if (argument.find_first_of(L" \t\"") == std::wstring::npos)
    {
        return argument;
    }
    std::wstring quoted(1, L'\"');
    size_t backslashes = 0;
    for (const wchar_t character : argument)
    {
        if (character == L'\\') { ++backslashes; continue; }
        if (character == L'\"')
        {
            quoted.append(backslashes * 2 + 1, L'\\');
            quoted.push_back(L'\"');
            backslashes = 0;
            continue;
        }
        quoted.append(backslashes, L'\\');
        backslashes = 0;
        quoted.push_back(character);
    }
    quoted.append(backslashes * 2, L'\\');
    quoted.push_back(L'\"');
    return quoted;
}

HRESULT LaunchClipPort(CommandKind kind, const std::wstring& directory)
{
    const MenuConfiguration configuration = ReadConfiguration();
    if (!configuration.Enabled || configuration.InstallDirectory.empty())
    {
        return HRESULT_FROM_WIN32(ERROR_NOT_READY);
    }
    const std::filesystem::path executable =
        std::filesystem::path(configuration.InstallDirectory) / ApplicationName;
    if (GetFileAttributesW(executable.c_str()) == INVALID_FILE_ATTRIBUTES)
    {
        return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
    }
    const wchar_t* option = kind == CommandKind::Source
        ? L"--quick-start-source" : L"--quick-start-destination";
    std::wstring commandLine = QuoteArgument(executable.wstring()) + L" " + option + L" " + QuoteArgument(directory);
    std::vector<wchar_t> mutableCommandLine(commandLine.begin(), commandLine.end());
    mutableCommandLine.push_back(L'\0');
    STARTUPINFOW startupInfo{sizeof(startupInfo)};
    PROCESS_INFORMATION processInformation{};
    if (!CreateProcessW(executable.c_str(), mutableCommandLine.data(), nullptr, nullptr, FALSE, 0,
        nullptr, configuration.InstallDirectory.c_str(), &startupInfo, &processInformation))
    {
        return HRESULT_FROM_WIN32(GetLastError());
    }
    CloseHandle(processInformation.hThread);
    CloseHandle(processInformation.hProcess);
    return S_OK;
}

class ExplorerCommand;

class CommandEnumerator final : public IEnumExplorerCommand
{
public:
    explicit CommandEnumerator(IUnknown* site, ULONG index = 0);
    ~CommandEnumerator();
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** object) override;
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references_; }
    ULONG STDMETHODCALLTYPE Release() override;
    HRESULT STDMETHODCALLTYPE Next(ULONG count, IExplorerCommand** commands, ULONG* fetched) override;
    HRESULT STDMETHODCALLTYPE Skip(ULONG count) override;
    HRESULT STDMETHODCALLTYPE Reset() override { index_ = 0; return S_OK; }
    HRESULT STDMETHODCALLTYPE Clone(IEnumExplorerCommand** enumerator) override;
private:
    std::atomic_ulong references_{1};
    std::array<IExplorerCommand*, 2> commands_{};
    ULONG index_ = 0;
    ComPtr<IUnknown> site_;
};

class ExplorerCommand final : public IExplorerCommand, public IObjectWithSite
{
public:
    explicit ExplorerCommand(CommandKind kind, IUnknown* site = nullptr) : kind_(kind), site_(site) { ++OutstandingObjects; }
    ~ExplorerCommand() { --OutstandingObjects; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** object) override
    {
        if (object == nullptr) return E_POINTER;
        *object = nullptr;
        if (riid == IID_IUnknown || riid == IID_IExplorerCommand) *object = static_cast<IExplorerCommand*>(this);
        else if (riid == IID_IObjectWithSite) *object = static_cast<IObjectWithSite*>(this);
        else return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references_; }
    ULONG STDMETHODCALLTYPE Release() override
    {
        const ULONG remaining = --references_;
        if (remaining == 0) delete this;
        return remaining;
    }
    HRESULT STDMETHODCALLTYPE GetTitle(IShellItemArray*, PWSTR* title) override
    {
        if (title == nullptr) return E_POINTER;
        const MenuConfiguration configuration = ReadConfiguration();
        return SHStrDupW(LocalizedTitle(kind_, configuration.Language), title);
    }
    HRESULT STDMETHODCALLTYPE GetIcon(IShellItemArray*, PWSTR* icon) override
    {
        if (icon == nullptr) return E_POINTER;
        *icon = nullptr;
        const MenuConfiguration configuration = ReadConfiguration();
        if (configuration.InstallDirectory.empty()) return E_NOTIMPL;
        const std::filesystem::path executable =
            std::filesystem::path(configuration.InstallDirectory) / ApplicationName;
        return SHStrDupW(executable.c_str(), icon);
    }
    HRESULT STDMETHODCALLTYPE GetToolTip(IShellItemArray*, PWSTR* tooltip) override
    {
        if (tooltip != nullptr) *tooltip = nullptr;
        return E_NOTIMPL;
    }
    HRESULT STDMETHODCALLTYPE GetCanonicalName(GUID* commandName) override
    {
        if (commandName == nullptr) return E_POINTER;
        *commandName = kind_ == CommandKind::Source ? SourceCommandGuid :
            kind_ == CommandKind::Destination ? DestinationCommandGuid : RootCommandClsid;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetState(IShellItemArray* selection, BOOL, EXPCMDSTATE* commandState) override
    {
        if (commandState == nullptr) return E_POINTER;
        if (!ReadConfiguration().Enabled) { *commandState = ECS_HIDDEN; return S_OK; }
        DWORD count = 0;
        if (selection != nullptr && (FAILED(selection->GetCount(&count)) || count > 1))
        {
            *commandState = ECS_HIDDEN; return S_OK;
        }
        if (count == 1)
        {
            std::wstring path;
            *commandState = TryGetSelectedDirectory(selection, path) ? ECS_ENABLED : ECS_HIDDEN;
            return S_OK;
        }
        // Directory-background selection can be empty; Explorer provides its folder through the site.
        *commandState = ECS_ENABLED;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Invoke(IShellItemArray* selection, IBindCtx*) override
    {
        if (kind_ == CommandKind::Root) return E_NOTIMPL;
        std::wstring path;
        if (!TryGetSelectedDirectory(selection, path) && !TryGetBackgroundDirectory(site_.Get(), path))
        {
            return HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND);
        }
        return LaunchClipPort(kind_, path);
    }
    HRESULT STDMETHODCALLTYPE GetFlags(EXPCMDFLAGS* flags) override
    {
        if (flags == nullptr) return E_POINTER;
        *flags = kind_ == CommandKind::Root ? ECF_HASSUBCOMMANDS : ECF_DEFAULT;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE EnumSubCommands(IEnumExplorerCommand** commands) override
    {
        if (commands == nullptr) return E_POINTER;
        *commands = nullptr;
        if (kind_ != CommandKind::Root) return E_NOTIMPL;
        auto enumerator = new (std::nothrow) CommandEnumerator(site_.Get());
        if (enumerator == nullptr) return E_OUTOFMEMORY;
        *commands = enumerator;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE SetSite(IUnknown* site) override { site_ = site; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetSite(REFIID riid, void** site) override
    {
        if (site == nullptr) return E_POINTER;
        *site = nullptr;
        return site_ == nullptr ? E_FAIL : site_->QueryInterface(riid, site);
    }
private:
    std::atomic_ulong references_{1};
    CommandKind kind_;
    ComPtr<IUnknown> site_;
};

CommandEnumerator::CommandEnumerator(IUnknown* site, ULONG index) : index_(index), site_(site)
{
    ++OutstandingObjects;
    commands_[0] = new (std::nothrow) ExplorerCommand(CommandKind::Source, site);
    commands_[1] = new (std::nothrow) ExplorerCommand(CommandKind::Destination, site);
}
CommandEnumerator::~CommandEnumerator()
{
    for (IExplorerCommand* command : commands_) if (command != nullptr) command->Release();
    --OutstandingObjects;
}
HRESULT STDMETHODCALLTYPE CommandEnumerator::QueryInterface(REFIID riid, void** object)
{
    if (object == nullptr) return E_POINTER;
    *object = nullptr;
    if (riid != IID_IUnknown && riid != IID_IEnumExplorerCommand) return E_NOINTERFACE;
    *object = static_cast<IEnumExplorerCommand*>(this); AddRef(); return S_OK;
}
ULONG STDMETHODCALLTYPE CommandEnumerator::Release()
{
    const ULONG remaining = --references_; if (remaining == 0) delete this; return remaining;
}
HRESULT STDMETHODCALLTYPE CommandEnumerator::Next(ULONG count, IExplorerCommand** commands, ULONG* fetched)
{
    if (commands == nullptr || (count != 1 && fetched == nullptr)) return E_POINTER;
    ULONG copied = 0;
    while (copied < count && index_ < commands_.size())
    {
        IExplorerCommand* command = commands_[index_++];
        if (command == nullptr) continue;
        command->AddRef(); commands[copied++] = command;
    }
    if (fetched != nullptr) *fetched = copied;
    return copied == count ? S_OK : S_FALSE;
}
HRESULT STDMETHODCALLTYPE CommandEnumerator::Skip(ULONG count)
{
    index_ = std::min<ULONG>(static_cast<ULONG>(commands_.size()), index_ + count);
    return index_ < commands_.size() ? S_OK : S_FALSE;
}
HRESULT STDMETHODCALLTYPE CommandEnumerator::Clone(IEnumExplorerCommand** enumerator)
{
    if (enumerator == nullptr) return E_POINTER;
    *enumerator = new (std::nothrow) CommandEnumerator(site_.Get(), index_);
    return *enumerator == nullptr ? E_OUTOFMEMORY : S_OK;
}

class CommandClassFactory final : public IClassFactory
{
public:
    CommandClassFactory() { ++OutstandingObjects; }
    ~CommandClassFactory() { --OutstandingObjects; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** object) override
    {
        if (object == nullptr) return E_POINTER;
        *object = nullptr;
        if (riid != IID_IUnknown && riid != IID_IClassFactory) return E_NOINTERFACE;
        *object = static_cast<IClassFactory*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references_; }
    ULONG STDMETHODCALLTYPE Release() override
    {
        const ULONG remaining = --references_; if (remaining == 0) delete this; return remaining;
    }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** object) override
    {
        if (outer != nullptr) return CLASS_E_NOAGGREGATION;
        auto command = new (std::nothrow) ExplorerCommand(CommandKind::Root);
        if (command == nullptr) return E_OUTOFMEMORY;
        const HRESULT result = command->QueryInterface(riid, object); command->Release(); return result;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override
    {
        if (lock) ++OutstandingObjects; else --OutstandingObjects;
        return S_OK;
    }
private:
    std::atomic_ulong references_{1};
};
}

STDAPI DllGetClassObject(REFCLSID classId, REFIID interfaceId, void** object)
{
    if (classId != RootCommandClsid) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = new (std::nothrow) CommandClassFactory();
    if (factory == nullptr) return E_OUTOFMEMORY;
    const HRESULT result = factory->QueryInterface(interfaceId, object); factory->Release(); return result;
}

STDAPI DllCanUnloadNow()
{
    return OutstandingObjects == 0 ? S_OK : S_FALSE;
}

BOOL APIENTRY DllMain(HMODULE, DWORD, LPVOID) { return TRUE; }
