#include "dialogs.h"

#include <windows.h>
#include <commctrl.h>

#include <charconv>
#include <string>

#pragma comment(linker, "/manifestdependency:\"type='win32' name='Microsoft.Windows.Common-Controls' " \
                        "version='6.0.0.0' processorArchitecture='*' publicKeyToken='6595b64144ccf1df' language='*'\"")

namespace {
constexpr int DownloadButton = 101;

bool Chinese()
{
    return PRIMARYLANGID(GetUserDefaultUILanguage()) == LANG_CHINESE;
}

struct Progress {
    std::filesystem::path directory;
    bool done = false;
    bool marquee = true;
};

bool ReadPercent(const std::filesystem::path& path, int& percent)
{
    const auto file = CreateFileW(path.c_str(), GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    char bytes[16];
    DWORD length = 0;
    const auto read = ReadFile(file, bytes, sizeof(bytes), &length, nullptr);
    CloseHandle(file);
    if (!read || length == 0 || length == sizeof(bytes)) return false;
    while (length > 0 && (bytes[length - 1] == '\r' || bytes[length - 1] == '\n')) --length;
    const auto parsed = std::from_chars(bytes, bytes + length, percent);
    return parsed.ec == std::errc{} && parsed.ptr == bytes + length && percent >= -1 && percent <= 100;
}

HRESULT CALLBACK UpdateProgress(HWND dialog, UINT notification, WPARAM, LPARAM, LONG_PTR data)
{
    auto& progress = *reinterpret_cast<Progress*>(data);
    if (notification == TDN_CREATED) {
        SendMessageW(dialog, TDM_SET_PROGRESS_BAR_MARQUEE, TRUE, 40);
    } else if (notification == TDN_TIMER) {
        std::error_code error;
        if (std::filesystem::exists(progress.directory / L"minirp-done", error)) {
            progress.done = true;
            SendMessageW(dialog, TDM_CLICK_BUTTON, IDCANCEL, 0);
            return S_OK;
        }
        int percent = -1;
        if (!ReadPercent(progress.directory / L"minirp-progress", percent)) return S_OK;
        const bool marquee = percent < 0;
        if (marquee != progress.marquee) {
            progress.marquee = marquee;
            SendMessageW(dialog, TDM_SET_MARQUEE_PROGRESS_BAR, marquee, 0);
            SendMessageW(dialog, TDM_SET_PROGRESS_BAR_MARQUEE, marquee, 40);
        }
        if (!marquee) SendMessageW(dialog, TDM_SET_PROGRESS_BAR_POS, percent, 0);
    }
    return S_OK;
}
}

int ConfirmBrowserDownload(std::wstring_view rawBytes)
{
    if (rawBytes.empty() || rawBytes.find_first_not_of(L"0123456789") != std::wstring_view::npos) return 1;
    std::string bytes;
    bytes.reserve(rawBytes.size());
    for (const auto character : rawBytes) bytes.push_back(static_cast<char>(character));
    unsigned long long size = 0;
    const auto parsed = std::from_chars(bytes.data(), bytes.data() + bytes.size(), size);
    if (parsed.ec != std::errc{} || size == 0) return 1;
    constexpr auto MiB = 1024ull * 1024;
    const auto amount = std::to_wstring(size / MiB + (size % MiB != 0));
    const auto chinese = Chinese();
    const auto copy = chinese
        ? L"MiniRP 需要 Chromium Embedded Framework 来打开 WebUI 或抓取网页。下载约 " + amount +
              L" MiB。下载完成后，可在离线状态下继续使用此运行时。"
        : L"MiniRP needs Chromium Embedded Framework to open the WebUI or fetch webpages. The download is about " + amount +
              L" MiB. Once installed, the runtime is available offline.";
    const TASKDIALOG_BUTTON buttons[] = {
        {DownloadButton, chinese ? L"下载并继续" : L"Download and continue"},
        {IDCANCEL, chinese ? L"稍后" : L"Later"}
    };
    TASKDIALOGCONFIG dialog{};
    dialog.cbSize = sizeof(dialog);
    dialog.dwFlags = TDF_ALLOW_DIALOG_CANCELLATION | TDF_SIZE_TO_CONTENT;
    dialog.pszWindowTitle = L"MiniRP";
    dialog.pszMainInstruction = chinese ? L"下载浏览器运行时？" : L"Download a browser runtime?";
    dialog.pszContent = copy.c_str();
    dialog.pButtons = buttons;
    dialog.cButtons = ARRAYSIZE(buttons);
    dialog.nDefaultButton = IDCANCEL;
    int selected = IDCANCEL;
    if (FAILED(TaskDialogIndirect(&dialog, &selected, nullptr, nullptr))) return 1;
    return selected == DownloadButton ? 0 : 2;
}

int BrowserDownloadProgress(const std::filesystem::path& directory)
{
    std::error_code error;
    if (!directory.is_absolute() || !std::filesystem::is_directory(directory, error)) return 1;
    Progress progress{directory};
    const auto chinese = Chinese();
    const TASKDIALOG_BUTTON cancel = {IDCANCEL, chinese ? L"取消" : L"Cancel"};
    TASKDIALOGCONFIG dialog{};
    dialog.cbSize = sizeof(dialog);
    dialog.dwFlags = TDF_SHOW_MARQUEE_PROGRESS_BAR | TDF_CALLBACK_TIMER |
        TDF_ALLOW_DIALOG_CANCELLATION | TDF_SIZE_TO_CONTENT;
    dialog.pszWindowTitle = L"MiniRP";
    dialog.pszMainInstruction = chinese ? L"正在准备浏览器运行时" : L"Preparing the browser runtime";
    dialog.pszContent = chinese ? L"MiniRP 会在启用前验证下载内容。" : L"MiniRP verifies the download before enabling it.";
    dialog.pButtons = &cancel;
    dialog.cButtons = 1;
    dialog.pfCallback = UpdateProgress;
    dialog.lpCallbackData = reinterpret_cast<LONG_PTR>(&progress);
    int selected = IDCANCEL;
    if (FAILED(TaskDialogIndirect(&dialog, &selected, nullptr, nullptr))) return 1;
    return progress.done ? 0 : 2;
}
