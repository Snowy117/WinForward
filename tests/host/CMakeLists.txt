find_package(GTest REQUIRED)
include(GoogleTest)

add_executable(wf_common_tests
    forwarded_blob_builder.h
    forwarded_blob_test.cpp
    forwarded_matcher_test.cpp
    matcher_test.cpp
    redirect_context_test.cpp
    rule_blob_builder.h
    rule_blob_test.cpp)
target_link_libraries(wf_common_tests PRIVATE wf_common GTest::gtest GTest::gtest_main)

gtest_discover_tests(wf_common_tests)

# 跨语言差分测试：读取 C# 侧（src/userland）生成的夹具，用同一份 try_open_blob + match
# 逐条断言决策一致。C# 侧默认把夹具写到 <仓库根>/build/diff-fixtures；本目标默认从
# ${CMAKE_BINARY_DIR}/diff-fixtures 读取（构建目录是 build 时两者一致）：
#   dotnet run --project src/userland/WinForward.Userland.Tests -- test
# 环境变量 WF_DIFF_FIXTURE_DIR 可覆盖两侧的默认目录。
add_executable(wf_differential_tests
    differential_fixture.h
    differential_test.cpp)
target_link_libraries(wf_differential_tests PRIVATE wf_common GTest::gtest GTest::gtest_main)
target_compile_definitions(wf_differential_tests PRIVATE
    WF_DIFF_FIXTURE_DIR_DEFAULT="${CMAKE_BINARY_DIR}/diff-fixtures")

gtest_discover_tests(wf_differential_tests)

# 一条命令把「生成夹具 → 跑全部 ctest」串起来。分开跑时 C++ 读的是上一次的夹具，
# 改了 C# 期望却不重跑生成会假绿 —— 这个 target 就是堵那个洞的：
#   cmake --build build --target wf_check
# dotnet 故意不 find_program：解析出来的绝对路径会被缓存进 CMakeCache，旧构建目录会一直调用
# 配置时那一份（可能来自宿主 profile）。这里直接用 PATH 上的 dotnet —— 在 devShell 里就是
# flake 提供的那份。
add_custom_target(wf_check
    COMMAND ${CMAKE_COMMAND} -E env
            WF_DIFF_FIXTURE_DIR=${CMAKE_BINARY_DIR}/diff-fixtures
            /bin/sh -c "command -v dotnet >/dev/null 2>&1 || { echo '找不到 dotnet：先进入仓库 devShell（flake.nix 提供 dotnet-sdk_10）' >&2; exit 1; }; echo \"wf_check: dotnet = $(command -v dotnet)\""
    COMMAND ${CMAKE_COMMAND} -E env
            WF_DIFF_FIXTURE_DIR=${CMAKE_BINARY_DIR}/diff-fixtures
            dotnet run
            --project ${CMAKE_CURRENT_SOURCE_DIR}/../../src/userland/WinForward.Userland.Tests
            -- test
    COMMAND ${CMAKE_COMMAND} -E env
            WF_DIFF_FIXTURE_DIR=${CMAKE_BINARY_DIR}/diff-fixtures
            ${CMAKE_CTEST_COMMAND} --output-on-failure
    WORKING_DIRECTORY ${CMAKE_BINARY_DIR}
    DEPENDS wf_differential_tests wf_common_tests
    COMMENT "生成 C# 差分夹具，再跑全部 ctest"
    VERBATIM
    USES_TERMINAL)
