cmake_minimum_required(VERSION 3.25)
project(MiniRPWebView2 LANGUAGES CXX)

if(NOT WIN32 OR NOT DEFINED WEBVIEW2_SDK OR NOT DEFINED WEBVIEW2_ARCH)
  message(FATAL_ERROR "A Windows WebView2 SDK and target architecture are required.")
endif()

add_executable(minirp-webview2 WIN32 host.cpp dialogs.cpp)
set_property(TARGET minirp-webview2 PROPERTY MSVC_RUNTIME_LIBRARY MultiThreaded)
target_compile_features(minirp-webview2 PRIVATE cxx_std_20)
target_compile_options(minirp-webview2 PRIVATE /utf-8 /W4)
target_compile_definitions(minirp-webview2 PRIVATE NOMINMAX UNICODE _UNICODE)
target_include_directories(minirp-webview2 SYSTEM PRIVATE "${WEBVIEW2_SDK}/build/native/include")
target_link_libraries(minirp-webview2 PRIVATE
  "${WEBVIEW2_SDK}/build/native/${WEBVIEW2_ARCH}/WebView2LoaderStatic.lib"
  comctl32 ole32 oleaut32 shell32 shlwapi user32 version)
