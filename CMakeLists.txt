cmake_minimum_required(VERSION 3.28)
project(winforward_kernel LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# 本项目不使用 C++20 模块。关掉扫描，否则 CMake 会把 -fmodules-ts 一类参数写进
# compile_commands.json，clang-tidy 的前端读不懂这些参数。
set(CMAKE_CXX_SCAN_FOR_MODULES OFF)

# 宿主侧只编译可移植核心与用户态代码；内核驱动在 Windows VM 上用 WDK 构建。
add_library(wf_warnings INTERFACE)
target_compile_options(wf_warnings INTERFACE -Wall -Wextra -Wpedantic -Werror)

enable_testing()

add_subdirectory(src/common)
add_subdirectory(tests/host)
