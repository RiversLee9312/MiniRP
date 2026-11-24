#include <windows.h>
#include <shellapi.h>
#include <wrl.h>
#include <WebView2.h>
#include <WebView2EnvironmentOptions.h>

#include "dialogs.h"

#include <charconv>
#include <filesystem>
#include <fstream>
#include <memory>
#include <string>
#include <string_view>
#include <type_traits>

namespace {
using Microsoft::WRL::Callback;
using Microsoft::WRL::ComPtr;
using Microsoft::WRL::Make;
namespace fs = std::filesystem;

enum class Mode { Probe, Window, Agent };
constexpr UINT_PTR StartupTimer = 1;
constexpr ULONGLONG StartupTimeoutMs = 20000;

bool WriteMarker(const fs::path& profile, const wchar_t* name, const std::string& value)
{
    auto marker = profile / name;
    auto pending = marker;
    pending += L".tmp";
    std::ofstream file(pending, std::ios::binary | std::ios::trunc);
    file << value << '\n';
    file.close();
    return file.good() && MoveFileExW(pending.c_str(), marker.c_str(), MOVEFILE_REPLACE_EXISTING);
}

std::string Version(ICoreWebView2Environment* environment)
{
    LPWSTR raw = nullptr;
    if (FAILED(environment->get_BrowserVersionString(&raw)) || !raw) return {};
    std::wstring version(raw);
    CoTaskMemFree(raw);
    if (version.find_first_not_of(L"0123456789.") != std::wstring::npos) return {};
    std::string encoded;
    encoded.reserve(version.size());
    for (const auto character : version) encoded.push_back(static_cast<char>(character));
    return encoded;
}

bool CompatibleVersion(std::string_view version)
{
    const auto dot = version.find('.');
    if (dot == std::string_view::npos) return false;
    int major = 0;
    const auto result = std::from_chars(version.data(), version.data() + dot, major);
    return result.ec == std::errc{} && result.ptr == version.data() + dot && major >= 135;
}

bool CorrectProfile(ICoreWebView2Environment* environment, const fs::path& profile)
{
    ComPtr<ICoreWebView2Environment7> configuration;
    LPWSTR folder = nullptr;
    if (FAILED(environment->QueryInterface(IID_PPV_ARGS(&configuration))) ||
        FAILED(configuration->get_UserDataFolder(&folder)) || !folder) return false;
    std::error_code error;
    const bool correct = fs::equivalent(folder, profile, error);
    CoTaskMemFree(folder);
    return correct;
}

bool IsHttp(std::wstring_view url)
{
    return url.starts_with(L"http://") || url.starts_with(L"https://");
}

std::wstring LoopbackOrigin(std::wstring_view url)
{
    constexpr std::wstring_view prefix = L"http://127.0.0.1:";
    if (!url.starts_with(prefix)) return {};
    const auto boundary = url.find_first_of(L"/?#", prefix.size());
    const auto origin = url.substr(0, boundary);
    const auto port = origin.substr(prefix.size());
    if (port.empty() || port.size() > 5 || port.find_first_not_of(L"0123456789") != port.npos)
        return {};
    return std::wstring(origin);
}

std::string Utf8(std::wstring_view value)
{
    if (value.empty()) return {};
    const auto length = static_cast<int>(value.size());
    auto bytes = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), length,
        nullptr, 0, nullptr, nullptr);
    std::string result(bytes, '\0');
    if (bytes != 0)
        WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), length, result.data(),
            bytes, nullptr, nullptr);
    return result;
}

fs::path RuntimeExecutable(ICoreWebView2* view)
{
    UINT32 id = 0;
    if (FAILED(view->get_BrowserProcessId(&id)) || !id) return {};
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, id);
    if (!process) return {};
    DWORD length = 32768;
    std::wstring path(length, L'\0');
    const auto found = QueryFullProcessImageNameW(process, 0, path.data(), &length);
    CloseHandle(process);
    if (!found) return {};
    path.resize(length);
    fs::path executable(path);
    return _wcsicmp(executable.filename().c_str(), L"msedgewebview2.exe") == 0 ? executable : fs::path{};
}

fs::path SelectedRuntime()
{
    constexpr auto name = L"MINIRP_WEBVIEW2_EXECUTABLE";
    const auto size = GetEnvironmentVariableW(name, nullptr, 0);
    if (size == 0 || size > 32768) return {};
    std::wstring value(size, L'\0');
    const auto length = GetEnvironmentVariableW(name, value.data(), size);
    if (length == 0 || length >= size) return {};
    value.resize(length);
    return fs::path(value);
}

class Host {
public:
    Host(Mode mode, fs::path profile, std::wstring url, fs::path selected)
        : mode_(mode), profile_(std::move(profile)), url_(std::move(url)),
          origin_(LoopbackOrigin(url_)), selected_(std::move(selected)) {}

    int Run(HINSTANCE instance)
    {
        const auto loadIcon = [instance](int widthMetric, int heightMetric) {
            const auto handle = static_cast<HICON>(LoadImageW(instance, MAKEINTRESOURCEW(MINIRP_WINDOW_ICON_ID),
                IMAGE_ICON, GetSystemMetrics(widthMetric), GetSystemMetrics(heightMetric), 0));
            return std::unique_ptr<std::remove_pointer_t<HICON>, decltype(&DestroyIcon)>(handle, DestroyIcon);
        };
        const auto largeIcon = loadIcon(SM_CXICON, SM_CYICON);
        const auto smallIcon = loadIcon(SM_CXSMICON, SM_CYSMICON);
        WNDCLASSEXW windowClass{};
        windowClass.cbSize = sizeof(windowClass);
        windowClass.lpfnWndProc = WindowProc;
        windowClass.hInstance = instance;
        windowClass.hIcon = largeIcon.get();
        windowClass.hIconSm = smallIcon.get();
        windowClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        windowClass.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
        windowClass.lpszClassName = L"MiniRP.WebView2";
        if (!RegisterClassExW(&windowClass)) return 1;

        window_ = CreateWindowExW(0, windowClass.lpszClassName, L"MiniRP", WS_OVERLAPPEDWINDOW,
            CW_USEDEFAULT, CW_USEDEFAULT, 1100, 780, nullptr, nullptr, instance, this);
        if (!window_) {
            UnregisterClassW(windowClass.lpszClassName, instance);
            return 1;
        }

        started_ = GetTickCount64();
        if (!SetTimer(window_, StartupTimer, 100, nullptr) || FAILED(Initialize())) Stop(1);
        MSG message{};
        while (GetMessageW(&message, nullptr, 0, 0) > 0) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        UnregisterClassW(windowClass.lpszClassName, instance);
        return exitCode_;
    }

private:
    HRESULT Initialize()
    {
        auto options = Make<CoreWebView2EnvironmentOptions>();
        ComPtr<ICoreWebView2EnvironmentOptions7> channels;
        if (!options || FAILED(options.As(&channels)) ||
            FAILED(channels->put_ReleaseChannels(COREWEBVIEW2_RELEASE_CHANNELS_STABLE))) return E_FAIL;

        if (mode_ == Mode::Agent) {
            auto result = options->put_AdditionalBrowserArguments(
                L"--remote-debugging-port=0 --remote-debugging-address=127.0.0.1 "
                L"--disable-background-timer-throttling --disable-renderer-backgrounding");
            if (FAILED(result)) return result;
        }

        const auto runtimeFolder = selected_.parent_path();
        return CreateCoreWebView2EnvironmentWithOptions(
            runtimeFolder.empty() ? nullptr : runtimeFolder.c_str(), profile_.c_str(), options.Get(),
            Callback<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>(
                [this](HRESULT result, ICoreWebView2Environment* environment) -> HRESULT {
                    if (closing_) return S_OK;
                    if (FAILED(result) || !environment || !CorrectProfile(environment, profile_)) return Stop(1);
                    version_ = Version(environment);
                    if (!CompatibleVersion(version_)) return Stop(1);
                    result = environment->CreateCoreWebView2Controller(window_,
                        Callback<ICoreWebView2CreateCoreWebView2ControllerCompletedHandler>(
                            [this](HRESULT result, ICoreWebView2Controller* controller) -> HRESULT {
                                if (closing_) return S_OK;
                                return FAILED(result) || !controller || FAILED(Load(controller)) ? Stop(1) : S_OK;
                            }).Get());
                    return FAILED(result) ? Stop(1) : S_OK;
                }).Get());
    }

    HRESULT Load(ICoreWebView2Controller* controller)
    {
        controller_ = controller;
        HRESULT result = controller_->get_CoreWebView2(&view_);
        if (FAILED(result) || !view_) return E_FAIL;
        const auto runtime = RuntimeExecutable(view_.Get());
        std::error_code error;
        if (runtime.empty() || (!selected_.empty() && !fs::equivalent(runtime, selected_, error)) ||
            !WriteMarker(profile_, L"minirp-runtime", Utf8(runtime.native()))) return E_FAIL;
        if (FAILED(Resize())) return E_FAIL;
        ComPtr<ICoreWebView2Settings> settings;
        if (FAILED(view_->get_Settings(&settings))) return E_FAIL;
        settings->put_IsStatusBarEnabled(FALSE);
        if (mode_ == Mode::Window) settings->put_AreDevToolsEnabled(FALSE);

        EventRegistrationToken registration{};
        if (mode_ == Mode::Window) {
            result = view_->add_NavigationStarting(
                Callback<ICoreWebView2NavigationStartingEventHandler>(
                    [this](ICoreWebView2*, ICoreWebView2NavigationStartingEventArgs* args) -> HRESULT {
                        LPWSTR uri = nullptr;
                        if (FAILED(args->get_Uri(&uri)) || !uri || LoopbackOrigin(uri) != origin_)
                            args->put_Cancel(TRUE);
                        CoTaskMemFree(uri);
                        return S_OK;
                    }).Get(), &registration);
            if (FAILED(result)) return result;
        }
        result = view_->add_NewWindowRequested(
            Callback<ICoreWebView2NewWindowRequestedEventHandler>(
                [this](ICoreWebView2*, ICoreWebView2NewWindowRequestedEventArgs* args) -> HRESULT {
                    args->put_Handled(TRUE);
                    BOOL requested = FALSE;
                    LPWSTR uri = nullptr;
                    if (SUCCEEDED(args->get_IsUserInitiated(&requested)) && requested &&
                        SUCCEEDED(args->get_Uri(&uri)) && uri && IsHttp(uri)) {
                        if (mode_ == Mode::Window)
                            ShellExecuteW(window_, L"open", uri, nullptr, nullptr, SW_SHOWNORMAL);
                        else if (mode_ == Mode::Agent)
                            view_->Navigate(uri);
                    }
                    CoTaskMemFree(uri);
                    return S_OK;
                }).Get(), &registration);
        if (FAILED(result)) return result;
        result = view_->add_NavigationCompleted(
            Callback<ICoreWebView2NavigationCompletedEventHandler>(
                [this](ICoreWebView2*, ICoreWebView2NavigationCompletedEventArgs* args) -> HRESULT {
                    if (loaded_) return S_OK;
                    BOOL success = FALSE;
                    if (FAILED(args->get_IsSuccess(&success)) || !success) return Stop(1);
                    loaded_ = true;
                    if (mode_ == Mode::Window) {
                        if (!SetWindowPos(window_, HWND_TOP, 0, 0, 0, 0,
                                SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW) ||
                            FAILED(Resize()) || !WriteMarker(profile_, L"minirp-version", version_)) return Stop(1);
                        KillTimer(window_, StartupTimer);
                    }
                    if (mode_ == Mode::Probe) return Probe();
                    return S_OK;
                }).Get(), &registration);
        if (FAILED(result)) return result;

        if (mode_ == Mode::Probe)
            return view_->NavigateToString(L"<!doctype html><title>MiniRP capability check</title>");
        return view_->Navigate(mode_ == Mode::Agent ? L"about:blank" : url_.c_str());
    }

    HRESULT Probe()
    {
        const auto result = view_->ExecuteScript(
            L"typeof EventSource==='function' && typeof ResizeObserver==='function' && "
            L"typeof File==='function' && CSS.supports('appearance','base-select')",
            Callback<ICoreWebView2ExecuteScriptCompletedHandler>(
                [this](HRESULT result, LPCWSTR value) -> HRESULT {
                    const auto ok = SUCCEEDED(result) && value && std::wstring_view(value) == L"true";
                    return Stop(ok && WriteMarker(profile_, L"minirp-version", version_) ? 0 : 1);
                }).Get());
        return FAILED(result) ? Stop(1) : S_OK;
    }

    void CheckAgentEndpoint()
    {
        std::ifstream portFile(profile_ / L"EBWebView" / L"DevToolsActivePort", std::ios::binary);
        std::string portText;
        if (!std::getline(portFile, portText) || portText.size() > 6) return;
        if (portText.ends_with('\r')) portText.pop_back();
        unsigned port = 0;
        const auto result = std::from_chars(portText.data(), portText.data() + portText.size(), port);
        if (result.ec != std::errc{} || result.ptr != portText.data() + portText.size() ||
            port < 1024 || port > 65535) return;
        if (!WriteMarker(profile_, L"minirp-version", version_) ||
            !WriteMarker(profile_, L"minirp-cdp", "http://127.0.0.1:" + std::to_string(port))) {
            Stop(1);
            return;
        }
        KillTimer(window_, StartupTimer);
    }

    HRESULT Resize()
    {
        if (!controller_) return S_OK;
        RECT bounds{};
        if (!GetClientRect(window_, &bounds)) return E_FAIL;
        auto result = controller_->put_Bounds(bounds);
        if (FAILED(result) || mode_ != Mode::Window) return result;
        // The controller is created before the containing HWND is shown.
        return controller_->put_IsVisible(IsWindowVisible(window_) && !IsIconic(window_));
    }

    HRESULT Stop(int exitCode)
    {
        exitCode_ = exitCode;
        // Close after the current WebView2 callback returns.
        PostMessageW(window_, WM_CLOSE, 0, 0);
        return S_OK;
    }

    static LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
    {
        auto* host = reinterpret_cast<Host*>(GetWindowLongPtrW(window, GWLP_USERDATA));
        if (message == WM_NCCREATE) {
            const auto* create = reinterpret_cast<CREATESTRUCTW*>(lParam);
            host = static_cast<Host*>(create->lpCreateParams);
            SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(host));
        }
        if (!host) return DefWindowProcW(window, message, wParam, lParam);
        switch (message) {
        case WM_SIZE:
            host->Resize();
            return 0;
        case WM_TIMER:
            if (wParam == StartupTimer) {
                if (GetTickCount64() - host->started_ >= StartupTimeoutMs) host->Stop(1);
                else if (host->mode_ == Mode::Agent && host->loaded_) host->CheckAgentEndpoint();
            }
            return 0;
        case WM_CLOSE:
            host->closing_ = true;
            if (host->controller_) host->controller_->Close();
            host->view_.Reset();
            host->controller_.Reset();
            DestroyWindow(window);
            return 0;
        case WM_DESTROY:
            PostQuitMessage(host->exitCode_);
            return 0;
        default:
            return DefWindowProcW(window, message, wParam, lParam);
        }
    }

    Mode mode_;
    fs::path profile_;
    std::wstring url_;
    std::wstring origin_;
    fs::path selected_;
    std::string version_;
    HWND window_ = nullptr;
    ULONGLONG started_ = 0;
    bool loaded_ = false;
    bool closing_ = false;
    int exitCode_ = 0;
    ComPtr<ICoreWebView2Controller> controller_;
    ComPtr<ICoreWebView2> view_;
};
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int)
{
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return 1;
    if (argc < 3 || argc > 4) {
        LocalFree(argv);
        return 2;
    }
    const std::wstring command = argv[1];
    const fs::path profile(argv[2]);
    const std::wstring url = argc == 4 ? argv[3] : L"";
    LocalFree(argv);
    if (argc == 3 && command == L"confirm") return ConfirmBrowserDownload(profile.native());
    if (argc == 3 && command == L"progress") return BrowserDownloadProgress(profile);
    const bool window = command == L"window";
    if ((!window && ((command != L"probe" && command != L"agent") || argc != 3)) ||
        (window && LoopbackOrigin(url).empty()) || !profile.is_absolute()) return 2;

    std::error_code error;
    fs::create_directories(profile, error);
    if (error) return 1;
    const auto runtime = command == L"probe" ? fs::path{} : SelectedRuntime();
    if (!runtime.empty() && (!runtime.is_absolute() || !fs::is_regular_file(runtime, error))) return 1;
    // These process-local overrides could merge two profiles or expose the UI.
    for (const auto* name : {L"WEBVIEW2_USER_DATA_FOLDER", L"WEBVIEW2_BROWSER_EXECUTABLE_FOLDER",
             L"WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", L"WEBVIEW2_PIPE_FOR_SCRIPT_DEBUGGER",
             L"WEBVIEW2_RELEASE_CHANNELS", L"WEBVIEW2_RELEASE_CHANNEL_PREFERENCE"})
        SetEnvironmentVariableW(name, nullptr);

    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    if (FAILED(OleInitialize(nullptr))) return 1;
    Host host(window ? Mode::Window : command == L"agent" ? Mode::Agent : Mode::Probe,
        profile, url, runtime);
    const auto exitCode = host.Run(instance);
    OleUninitialize();
    return exitCode;
}
