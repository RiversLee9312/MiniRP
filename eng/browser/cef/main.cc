#include <algorithm>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <string>
#include <utility>
#include <vector>

#include "include/cef_app.h"
#include "include/cef_browser.h"
#include "include/cef_command_line.h"
#include "include/cef_download_handler.h"
#include "include/cef_task.h"
#include "include/views/cef_browser_view.h"
#include "include/views/cef_window.h"

#if defined(OS_WIN)
#include "include/cef_sandbox_win.h"
#endif

namespace {

namespace fs = std::filesystem;

enum class Mode { kProbe, kProbeWindow, kWindow, kAgent };

bool Windowed(Mode mode) {
  return mode == Mode::kWindow || mode == Mode::kProbeWindow;
}

void Publish(const fs::path& directory,
             const char* name,
             const std::string& contents) {
  const auto file = directory / name;
  auto temporary = file;
  temporary += ".tmp";
  std::ofstream writer(temporary);
  writer << contents << '\n';
  writer.close();
  if (writer) {
    std::error_code error;
    fs::rename(temporary, file, error);
  }
}

class PublishDevToolsEndpoint final : public CefTask {
 public:
  PublishDevToolsEndpoint(fs::path profile, CefRefPtr<CefBrowser> browser)
      : profile_(std::move(profile)), browser_(std::move(browser)) {}

  void Execute() override {
    if (!browser_->IsValid()) return;

    std::ifstream port_file(profile_ / "DevToolsActivePort");
    unsigned int port = 0;
    if (port_file >> port && port >= 1024 && port <= 65535) {
      Publish(profile_, "minirp-cdp", "http://127.0.0.1:" + std::to_string(port));
    } else if (--remaining_attempts_ > 0) {
      CefPostDelayedTask(TID_UI, this, 50);
    } else {
      browser_->GetHost()->CloseBrowser(true);
    }
  }

 private:
  const fs::path profile_;
  const CefRefPtr<CefBrowser> browser_;
  int remaining_attempts_ = 300;

  IMPLEMENT_REFCOUNTING(PublishDevToolsEndpoint);
};

class CloseBrowser final : public CefTask {
 public:
  explicit CloseBrowser(CefRefPtr<CefBrowser> browser) : browser_(std::move(browser)) {}

  void Execute() override {
    if (browser_->IsValid()) browser_->GetHost()->CloseBrowser(true);
  }

 private:
  const CefRefPtr<CefBrowser> browser_;

  IMPLEMENT_REFCOUNTING(CloseBrowser);
};

class BrowserClient final : public CefClient,
                            public CefLifeSpanHandler,
                            public CefLoadHandler,
                            public CefRenderHandler,
                            public CefDownloadHandler {
 public:
  BrowserClient(Mode mode, fs::path profile, int& result)
      : mode_(mode), profile_(std::move(profile)), result_(result) {}

  CefRefPtr<CefLifeSpanHandler> GetLifeSpanHandler() override { return this; }
  CefRefPtr<CefLoadHandler> GetLoadHandler() override { return this; }
  CefRefPtr<CefRenderHandler> GetRenderHandler() override { return this; }
  CefRefPtr<CefDownloadHandler> GetDownloadHandler() override { return this; }

  void OnAfterCreated(CefRefPtr<CefBrowser> browser) override {
    browsers_.push_back(browser);
    if (primary_id_ == 0) {
      primary_id_ = browser->GetIdentifier();
      if (mode_ == Mode::kAgent)
        CefPostTask(TID_UI, new PublishDevToolsEndpoint(profile_, browser));
    }
  }

  bool OnBeforePopup(CefRefPtr<CefBrowser>,
                     CefRefPtr<CefFrame>,
                     int,
                     const CefString&,
                     const CefString&,
                     WindowOpenDisposition,
                     bool,
                     const CefPopupFeatures&,
                     CefWindowInfo&,
                     CefRefPtr<CefClient>& client,
                     CefBrowserSettings&,
                     CefRefPtr<CefDictionaryValue>&,
                     bool*) override {
    client = this;
    return closing_;
  }

  void OnLoadEnd(CefRefPtr<CefBrowser> browser,
                 CefRefPtr<CefFrame> frame,
                 int status) override {
    if (browser->GetIdentifier() != primary_id_ || !frame->IsMain() || status >= 400) return;

    if (mode_ == Mode::kProbe || mode_ == Mode::kProbeWindow) {
      Publish(profile_, "minirp-probe", "ready");
      result_ = 0;
      CefPostTask(TID_UI, new CloseBrowser(browser));
    } else if (mode_ == Mode::kWindow && !window_ready_) {
      window_ready_ = true;
      result_ = 0;
      Publish(profile_, "minirp-window", "ready");
    }
  }

  void OnBeforeClose(CefRefPtr<CefBrowser> browser) override {
    const int id = browser->GetIdentifier();
    std::erase_if(browsers_, [id](const auto& item) { return item->GetIdentifier() == id; });
    if (browsers_.empty()) {
      CefQuitMessageLoop();
    } else if (id == primary_id_) {
      closing_ = true;
      for (const auto& popup : browsers_)
        CefPostTask(TID_UI, new CloseBrowser(popup));
    }
  }

  void GetViewRect(CefRefPtr<CefBrowser>, CefRect& rect) override {
    rect = CefRect(0, 0, 1280, 900);
  }

  void OnPaint(CefRefPtr<CefBrowser>,
               PaintElementType,
               const RectList&,
               const void*,
               int,
               int) override {}

  bool CanDownload(CefRefPtr<CefBrowser>,
                   const CefString&,
                   const CefString&) override {
    return mode_ == Mode::kWindow;
  }

  bool OnBeforeDownload(CefRefPtr<CefBrowser>,
                        CefRefPtr<CefDownloadItem>,
                        const CefString& name,
                        CefRefPtr<CefBeforeDownloadCallback> callback) override {
    if (mode_ == Mode::kWindow) callback->Continue(name, true);
    return true;
  }

 private:
  const Mode mode_;
  const fs::path profile_;
  int& result_;
  std::vector<CefRefPtr<CefBrowser>> browsers_;
  int primary_id_ = 0;
  bool window_ready_ = false;
  bool closing_ = false;

  IMPLEMENT_REFCOUNTING(BrowserClient);
};

class Window final : public CefWindowDelegate {
 public:
  Window(CefRefPtr<CefBrowserView> view, bool show) : view_(std::move(view)), show_(show) {}

  void OnWindowCreated(CefRefPtr<CefWindow> window) override {
    window->SetTitle("MiniRP");
    window->AddChildView(view_);
    window->CenterWindow(CefSize(1150, 820));
    if (show_) {
      window->Show();
      view_->RequestFocus();
    }
  }

  void OnWindowDestroyed(CefRefPtr<CefWindow>) override { view_ = nullptr; }

  bool CanClose(CefRefPtr<CefWindow>) override {
    const auto browser = view_ ? view_->GetBrowser() : nullptr;
    return !browser || browser->GetHost()->TryCloseBrowser();
  }

 private:
  CefRefPtr<CefBrowserView> view_;
  const bool show_;

  IMPLEMENT_REFCOUNTING(Window);
};

class BrowserApp final : public CefApp, public CefBrowserProcessHandler {
 public:
  BrowserApp(Mode mode, fs::path profile, CefString url, int& result)
      : mode_(mode),
        profile_(std::move(profile)),
        url_(std::move(url)),
        result_(result) {}

  CefRefPtr<CefBrowserProcessHandler> GetBrowserProcessHandler() override {
    return this;
  }

  void OnBeforeCommandLineProcessing(const CefString& process_type,
                                     CefRefPtr<CefCommandLine> command) override {
    if (!process_type.empty()) return;
    command->AppendSwitch("disable-background-networking");
    command->AppendSwitch("disable-extensions");
    command->AppendSwitch("disable-component-extensions-with-background-pages");
    if (Windowed(mode_)) {
#if defined(OS_LINUX)
      if (std::getenv("WAYLAND_DISPLAY") && !std::getenv("DISPLAY"))
        command->AppendSwitchWithValue("ozone-platform", "wayland");
#endif
      return;
    }

    command->AppendSwitch("disable-gpu");
#if defined(OS_LINUX)
    command->AppendSwitchWithValue("ozone-platform", "headless");
#endif
    if (mode_ == Mode::kAgent)
      command->AppendSwitchWithValue("remote-debugging-port", "0");
  }

  void OnContextInitialized() override {
    CefRefPtr<BrowserClient> client = new BrowserClient(mode_, profile_, result_);
    CefBrowserSettings browser_settings;
    if (Windowed(mode_)) {
      const auto view = CefBrowserView::CreateBrowserView(
          client, url_, browser_settings, nullptr, nullptr, nullptr);
      if (view && CefWindow::CreateTopLevelWindow(new Window(view, mode_ == Mode::kWindow))) return;
    } else {
      CefWindowInfo window;
      window.SetAsWindowless(kNullWindowHandle);
      window.runtime_style = CEF_RUNTIME_STYLE_ALLOY;
      if (CefBrowserHost::CreateBrowser(window, client, url_, browser_settings,
                                        nullptr, nullptr)) {
        if (mode_ == Mode::kAgent) result_ = 0;
        return;
      }
    }
    CefQuitMessageLoop();
  }

 private:
  const Mode mode_;
  const fs::path profile_;
  const CefString url_;
  int& result_;

  IMPLEMENT_REFCOUNTING(BrowserApp);
};

int Run(const CefMainArgs& args,
        CefRefPtr<CefCommandLine> command,
        void* sandbox_info) {
  CefCommandLine::ArgumentList arguments;
  command->GetArguments(arguments);
  if (arguments.size() < 2) return 2;

  Mode mode;
  const auto action = arguments[0].ToString();
  if (action == "probe") {
    mode = Mode::kProbe;
  } else if (action == "probe-window") {
    mode = Mode::kProbeWindow;
  } else if (action == "agent") {
    mode = Mode::kAgent;
  } else if (action == "window" && arguments.size() == 3) {
    mode = Mode::kWindow;
  } else {
    return 2;
  }

  const auto profile = fs::u8path(arguments[1].ToString());
  if (!profile.is_absolute() || !fs::is_directory(profile)) return 2;
  const CefString url = mode == Mode::kWindow ? arguments[2] :
      mode == Mode::kAgent ? "about:blank" : "data:text/html,<title>MiniRP browser check</title>";

  CefSettings settings;
  settings.command_line_args_disabled = true;
  settings.windowless_rendering_enabled = !Windowed(mode);
  settings.log_severity = LOGSEVERITY_DISABLE;
  CefString(&settings.cache_path) = profile.native();
  CefString(&settings.root_cache_path) = profile.native();
  int result = 1;
  CefRefPtr<BrowserApp> app = new BrowserApp(mode, profile, url, result);
  if (!CefInitialize(args, settings, app, sandbox_info)) {
    const auto code = CefGetExitCode();
    return code == 0 ? 1 : code;
  }

  CefRunMessageLoop();
  CefShutdown();
  return result;
}

}  // namespace

#if defined(OS_WIN)
CEF_BOOTSTRAP_EXPORT int RunWinMain(HINSTANCE instance,
                                    LPWSTR,
                                    int,
                                    void* sandbox_info,
                                    cef_version_info_t*) {
  CefMainArgs args(instance);
  const auto code = CefExecuteProcess(args, nullptr, sandbox_info);
  if (code >= 0) return code;

  const auto command = CefCommandLine::CreateCommandLine();
  command->InitFromString(GetCommandLineW());
  return Run(args, command, sandbox_info);
}
#else
NO_STACK_PROTECTOR int main(int argc, char* argv[]) {
  CefMainArgs args(argc, argv);
  const auto code = CefExecuteProcess(args, nullptr, nullptr);
  if (code >= 0) return code;

  const auto command = CefCommandLine::CreateCommandLine();
  command->InitFromArgv(argc, argv);
  return Run(args, command, nullptr);
}
#endif
