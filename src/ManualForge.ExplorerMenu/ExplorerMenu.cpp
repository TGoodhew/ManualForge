// "Add to ManualForge library" on Windows 11's first right-click menu (#89).
//
// Explorer lists only IExplorerCommand handlers declared by a package there, so the installer registers a
// sparse package that gives ManualForge's folder an identity and declares this class for .pdf files. Explorer
// loads this DLL into a COM surrogate and hands it the selection; all it does is start ManualForge.App.exe,
// beside it, with --add and the paths, exactly as Send To does. The app takes it from there: a launch while
// the window is open hands its files to that window.
//
// The app is started by Explorer rather than by this process. The surrogate runs with the package's identity,
// and a process it started itself could inherit it; ManualForge.App is an unpackaged app on the Windows App
// Runtime, and is meant to start the same way whichever menu it came from.

#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <exdisp.h>
#include <shldisp.h>
#include <shlwapi.h>
#include <shobjidl_core.h>
#include <wrl/client.h>
#include <wrl/implements.h>
#include <wrl/module.h>

#include <string>
#include <vector>

using Microsoft::WRL::ClassicCom;
using Microsoft::WRL::ComPtr;
using Microsoft::WRL::InProc;
using Microsoft::WRL::Module;
using Microsoft::WRL::RuntimeClass;
using Microsoft::WRL::RuntimeClassFlags;

namespace
{
    HMODULE g_module = nullptr;

    // Command lines handed to Explorer are kept well short of the 32,767 characters CreateProcess allows;
    // a selection longer than that goes as several launches, which the open window gathers into one queue.
    constexpr size_t MaxArguments = 8000;

    std::wstring ModuleFolder()
    {
        std::wstring path(MAX_PATH, L'\0');
        for (;;)
        {
            const auto length = GetModuleFileNameW(g_module, path.data(), static_cast<DWORD>(path.size()));
            if (length == 0)
                return {};
            if (length < path.size())
            {
                path.resize(length);
                break;
            }
            path.resize(path.size() * 2);
        }

        return path.substr(0, path.find_last_of(L'\\'));
    }

    std::wstring AppPath() { return ModuleFolder() + L"\\ManualForge.App.exe"; }

    // A file system path holds no quotes and does not end in a backslash, so quoting is all it needs.
    std::wstring Quote(const std::wstring& path) { return L"\"" + path + L"\""; }

    // The desktop's own IShellDispatch2, through which a program is started by Explorer: Raymond Chen's
    // "How can I launch an unelevated process from my elevated process", for the same reason.
    HRESULT DesktopShell(IShellDispatch2** shell)
    {
        *shell = nullptr;

        ComPtr<IShellWindows> windows;
        auto hr = CoCreateInstance(CLSID_ShellWindows, nullptr, CLSCTX_LOCAL_SERVER, IID_PPV_ARGS(&windows));
        if (FAILED(hr))
            return hr;

        VARIANT none{};
        long window = 0;
        ComPtr<IDispatch> desktop;
        hr = windows->FindWindowSW(&none, &none, SWC_DESKTOP, &window, SWFO_NEEDDISPATCH, &desktop);
        if (hr != S_OK)
            return FAILED(hr) ? hr : E_FAIL;

        ComPtr<IServiceProvider> services;
        ComPtr<IShellBrowser> browser;
        ComPtr<IShellView> view;
        ComPtr<IDispatch> background;
        ComPtr<IShellFolderViewDual> folderView;
        ComPtr<IDispatch> application;
        if (FAILED(hr = desktop.As(&services))
            || FAILED(hr = services->QueryService(SID_STopLevelBrowser, IID_PPV_ARGS(&browser)))
            || FAILED(hr = browser->QueryActiveShellView(&view))
            || FAILED(hr = view->GetItemObject(SVGIO_BACKGROUND, IID_PPV_ARGS(&background)))
            || FAILED(hr = background.As(&folderView))
            || FAILED(hr = folderView->get_Application(&application)))
        {
            return hr;
        }

        return application->QueryInterface(IID_PPV_ARGS(shell));
    }

    HRESULT Launch(const std::wstring& arguments)
    {
        const auto app = AppPath();
        const auto folder = ModuleFolder();

        ComPtr<IShellDispatch2> shell;
        if (SUCCEEDED(DesktopShell(&shell)))
        {
            VARIANT parameters{}, directory{}, verb{}, show{};
            parameters.vt = VT_BSTR;
            parameters.bstrVal = SysAllocString(arguments.c_str());
            directory.vt = VT_BSTR;
            directory.bstrVal = SysAllocString(folder.c_str());
            verb.vt = VT_BSTR;
            verb.bstrVal = SysAllocString(L"open");
            show.vt = VT_I4;
            show.lVal = SW_SHOWNORMAL;
            const auto file = SysAllocString(app.c_str());

            const auto hr = shell->ShellExecute(file, parameters, directory, verb, show);

            SysFreeString(file);
            VariantClear(&parameters);
            VariantClear(&directory);
            VariantClear(&verb);
            if (SUCCEEDED(hr))
                return hr;
        }

        // No desktop to ask (Explorer restarting, say): start it from here rather than not at all.
        SHELLEXECUTEINFOW info{ sizeof(info) };
        info.fMask = SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI;
        info.lpVerb = L"open";
        info.lpFile = app.c_str();
        info.lpParameters = arguments.c_str();
        info.lpDirectory = folder.c_str();
        info.nShow = SW_SHOWNORMAL;
        return ShellExecuteExW(&info) ? S_OK : HRESULT_FROM_WIN32(GetLastError());
    }
}

class __declspec(uuid("4930d037-f987-4d71-ad01-63a7ea88a20c")) AddToLibraryCommand final
    : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IExplorerCommand, IObjectWithSite>
{
public:
    IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* name) override
    {
        return SHStrDupW(L"Add to ManualForge library", name);
    }

    IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* icon) override
    {
        return SHStrDupW((AppPath() + L",0").c_str(), icon);
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* tip) override
    {
        *tip = nullptr;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP GetCanonicalName(GUID* name) override
    {
        *name = __uuidof(AddToLibraryCommand);
        return S_OK;
    }

    IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state) override
    {
        *state = ECS_ENABLED;
        return S_OK;
    }

    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
    {
        if (items == nullptr)
            return S_OK;

        DWORD count = 0;
        auto hr = items->GetCount(&count);
        if (FAILED(hr))
            return hr;

        std::vector<std::wstring> paths;
        for (DWORD i = 0; i < count; ++i)
        {
            ComPtr<IShellItem> item;
            PWSTR path = nullptr;
            if (SUCCEEDED(items->GetItemAt(i, &item)) && SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)))
            {
                paths.emplace_back(path);
                CoTaskMemFree(path);
            }
        }

        // Anything the app is not given a reason about (not a PDF, a folder) it says so itself.
        for (size_t next = 0; next < paths.size();)
        {
            std::wstring arguments = L"--add";
            do
            {
                arguments += L" " + Quote(paths[next++]);
            } while (next < paths.size() && arguments.size() + paths[next].size() + 3 < MaxArguments);

            hr = Launch(arguments);
            if (FAILED(hr))
                return hr;
        }

        return S_OK;
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
    {
        *flags = ECF_DEFAULT;
        return S_OK;
    }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override
    {
        *commands = nullptr;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP SetSite(IUnknown* site) override
    {
        _site = site;
        return S_OK;
    }

    IFACEMETHODIMP GetSite(REFIID riid, void** site) override
    {
        return _site.CopyTo(riid, site);
    }

private:
    ComPtr<IUnknown> _site;
};

CoCreatableClass(AddToLibraryCommand);

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, _COM_Outptr_ void** instance)
{
    return Module<InProc>::GetModule().GetClassObject(clsid, riid, instance);
}

STDAPI DllCanUnloadNow()
{
    return Module<InProc>::GetModule().Terminate() ? S_OK : S_FALSE;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }

    return TRUE;
}
