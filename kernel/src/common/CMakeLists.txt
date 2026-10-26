add_library(wf_common INTERFACE)
target_include_directories(wf_common INTERFACE ${CMAKE_CURRENT_SOURCE_DIR})
target_sources(wf_common INTERFACE
    ${CMAKE_CURRENT_SOURCE_DIR}/forwarded_blob.h
    ${CMAKE_CURRENT_SOURCE_DIR}/forwarded_matcher.h
    ${CMAKE_CURRENT_SOURCE_DIR}/matcher.h
    ${CMAKE_CURRENT_SOURCE_DIR}/redirect_context.h
    ${CMAKE_CURRENT_SOURCE_DIR}/rule_blob.h
    ${CMAKE_CURRENT_SOURCE_DIR}/v4_mapped.h)
target_link_libraries(wf_common INTERFACE wf_warnings)
