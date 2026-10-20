find_package(GTest REQUIRED)
include(GoogleTest)

add_executable(wf_common_tests
    matcher_test.cpp
    redirect_context_test.cpp
    rule_blob_builder.h
    rule_blob_test.cpp)
target_link_libraries(wf_common_tests PRIVATE wf_common GTest::gtest GTest::gtest_main)

gtest_discover_tests(wf_common_tests)
