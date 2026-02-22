cmake_minimum_required(VERSION 3.16)

project(boringssl_net_curlimp_shim C)

set(CURL_IMPERSONATE_INCLUDE_DIR "" CACHE PATH "curl-impersonate include directory containing curl/curl.h")
set(CURL_IMPERSONATE_LIBRARY "" CACHE FILEPATH "Path to curl-impersonate shared library")

if(NOT CURL_IMPERSONATE_INCLUDE_DIR)
  message(FATAL_ERROR "CURL_IMPERSONATE_INCLUDE_DIR is required.")
endif()

if(NOT CURL_IMPERSONATE_LIBRARY)
  message(FATAL_ERROR "CURL_IMPERSONATE_LIBRARY is required.")
endif()

add_library(boringssl_net_curlimp_shim SHARED curl_impersonate_shim.c)
target_include_directories(boringssl_net_curlimp_shim PRIVATE "${CURL_IMPERSONATE_INCLUDE_DIR}")
target_link_libraries(boringssl_net_curlimp_shim PRIVATE "${CURL_IMPERSONATE_LIBRARY}")

set_target_properties(
  boringssl_net_curlimp_shim
  PROPERTIES
    OUTPUT_NAME "boringssl_net_curlimp_shim"
)

if(APPLE)
  target_link_options(boringssl_net_curlimp_shim PRIVATE "-Wl,-rpath,@loader_path")
elseif(UNIX)
  target_link_options(boringssl_net_curlimp_shim PRIVATE "-Wl,-rpath,$ORIGIN")
endif()
