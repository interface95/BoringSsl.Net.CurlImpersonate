#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUILD_DIR="${ROOT_DIR}/.work/curl-impersonate-shim-build"
INSTALL_DIR="${ROOT_DIR}/.work/curl-impersonate-shim-install"

# You can override these paths for your local curl-impersonate build.
INCLUDE_DIR="${CURL_IMPERSONATE_INCLUDE_DIR:-${ROOT_DIR}/.work/curl-impersonate-arm64-build/build-arm64/curl-8.1.1/include}"
LIBRARY_PATH="${CURL_IMPERSONATE_LIBRARY_PATH:-${ROOT_DIR}/.work/curl-impersonate-arm64-build/install-arm64/lib/libcurl-impersonate-chrome.4.dylib}"

if [[ ! -f "${INCLUDE_DIR}/curl/curl.h" ]]; then
  echo "Missing curl headers at ${INCLUDE_DIR}/curl/curl.h" >&2
  echo "Set CURL_IMPERSONATE_INCLUDE_DIR to your curl-impersonate include directory." >&2
  exit 1
fi

if [[ ! -f "${LIBRARY_PATH}" ]]; then
  echo "Missing curl library at ${LIBRARY_PATH}" >&2
  echo "Set CURL_IMPERSONATE_LIBRARY_PATH to your curl-impersonate shared library path." >&2
  exit 1
fi

cmake -S "${ROOT_DIR}/native/curl_impersonate_shim" \
  -B "${BUILD_DIR}" \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_INSTALL_PREFIX="${INSTALL_DIR}" \
  -DCURL_IMPERSONATE_INCLUDE_DIR="${INCLUDE_DIR}" \
  -DCURL_IMPERSONATE_LIBRARY="${LIBRARY_PATH}"

cmake --build "${BUILD_DIR}" --config Release

mkdir -p "${INSTALL_DIR}/lib"

if [[ -f "${BUILD_DIR}/libboringssl_net_curlimp_shim.dylib" ]]; then
  cp "${BUILD_DIR}/libboringssl_net_curlimp_shim.dylib" "${INSTALL_DIR}/lib/"
elif [[ -f "${BUILD_DIR}/libboringssl_net_curlimp_shim.so" ]]; then
  cp "${BUILD_DIR}/libboringssl_net_curlimp_shim.so" "${INSTALL_DIR}/lib/"
elif [[ -f "${BUILD_DIR}/Release/boringssl_net_curlimp_shim.dll" ]]; then
  cp "${BUILD_DIR}/Release/boringssl_net_curlimp_shim.dll" "${INSTALL_DIR}/lib/"
elif [[ -f "${BUILD_DIR}/boringssl_net_curlimp_shim.dll" ]]; then
  cp "${BUILD_DIR}/boringssl_net_curlimp_shim.dll" "${INSTALL_DIR}/lib/"
else
  echo "Failed to locate built shim artifact in ${BUILD_DIR}." >&2
  exit 1
fi

echo "Shim built at ${INSTALL_DIR}/lib"
