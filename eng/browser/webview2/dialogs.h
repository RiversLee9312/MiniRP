#pragma once

#include <filesystem>
#include <string_view>

int ConfirmBrowserDownload(std::wstring_view bytes);
int BrowserDownloadProgress(const std::filesystem::path& directory);
