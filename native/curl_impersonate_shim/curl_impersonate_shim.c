#include <curl/curl.h>

#if defined(_WIN32)
#define BSSL_CURL_SHIM_EXPORT __declspec(dllexport)
#else
#define BSSL_CURL_SHIM_EXPORT __attribute__((visibility("default")))
#endif

BSSL_CURL_SHIM_EXPORT CURLcode bsn_curl_global_init(long flags) {
    return curl_global_init(flags);
}

BSSL_CURL_SHIM_EXPORT CURL *bsn_curl_easy_init(void) {
    return curl_easy_init();
}

BSSL_CURL_SHIM_EXPORT void bsn_curl_easy_cleanup(CURL *easy_handle) {
    curl_easy_cleanup(easy_handle);
}

BSSL_CURL_SHIM_EXPORT void bsn_curl_easy_reset(CURL *easy_handle) {
    curl_easy_reset(easy_handle);
}

BSSL_CURL_SHIM_EXPORT CURLcode bsn_curl_easy_setopt_long(
    CURL *easy_handle,
    CURLoption option,
    long value) {
    return curl_easy_setopt(easy_handle, option, value);
}

BSSL_CURL_SHIM_EXPORT CURLcode bsn_curl_easy_setopt_ptr(
    CURL *easy_handle,
    CURLoption option,
    void *value) {
    return curl_easy_setopt(easy_handle, option, value);
}

BSSL_CURL_SHIM_EXPORT CURLcode bsn_curl_easy_setopt_str(
    CURL *easy_handle,
    CURLoption option,
    const char *value) {
    return curl_easy_setopt(easy_handle, option, value);
}

BSSL_CURL_SHIM_EXPORT CURLcode bsn_curl_easy_setopt_write_callback(
    CURL *easy_handle,
    CURLoption option,
    curl_write_callback callback) {
    return curl_easy_setopt(easy_handle, option, callback);
}

BSSL_CURL_SHIM_EXPORT CURLcode bsn_curl_easy_setopt_xferinfo_callback(
    CURL *easy_handle,
    CURLoption option,
    curl_xferinfo_callback callback) {
    return curl_easy_setopt(easy_handle, option, callback);
}

BSSL_CURL_SHIM_EXPORT CURLcode bsn_curl_easy_perform(CURL *easy_handle) {
    return curl_easy_perform(easy_handle);
}

BSSL_CURL_SHIM_EXPORT CURLcode bsn_curl_easy_getinfo_long(
    CURL *easy_handle,
    CURLINFO info,
    long *value) {
    return curl_easy_getinfo(easy_handle, info, value);
}

BSSL_CURL_SHIM_EXPORT const char *bsn_curl_easy_strerror(CURLcode code) {
    return curl_easy_strerror(code);
}

BSSL_CURL_SHIM_EXPORT struct curl_slist *bsn_curl_slist_append(
    struct curl_slist *list,
    const char *header) {
    return curl_slist_append(list, header);
}

BSSL_CURL_SHIM_EXPORT void bsn_curl_slist_free_all(struct curl_slist *list) {
    curl_slist_free_all(list);
}

BSSL_CURL_SHIM_EXPORT CURLcode bsn_curl_easy_impersonate(
    CURL *easy_handle,
    const char *target,
    int default_headers) {
    return curl_easy_impersonate(easy_handle, target, default_headers);
}

BSSL_CURL_SHIM_EXPORT CURLM *bsn_curl_multi_init(void) {
    return curl_multi_init();
}

BSSL_CURL_SHIM_EXPORT CURLMcode bsn_curl_multi_cleanup(CURLM *multi_handle) {
    return curl_multi_cleanup(multi_handle);
}

BSSL_CURL_SHIM_EXPORT CURLMcode bsn_curl_multi_add_handle(
    CURLM *multi_handle,
    CURL *easy_handle) {
    return curl_multi_add_handle(multi_handle, easy_handle);
}

BSSL_CURL_SHIM_EXPORT CURLMcode bsn_curl_multi_remove_handle(
    CURLM *multi_handle,
    CURL *easy_handle) {
    return curl_multi_remove_handle(multi_handle, easy_handle);
}

BSSL_CURL_SHIM_EXPORT CURLMcode bsn_curl_multi_perform(
    CURLM *multi_handle,
    int *running_handles) {
    return curl_multi_perform(multi_handle, running_handles);
}

BSSL_CURL_SHIM_EXPORT CURLMcode bsn_curl_multi_poll(
    CURLM *multi_handle,
    int timeout_ms,
    int *numfds) {
    return curl_multi_poll(multi_handle, NULL, 0, timeout_ms, numfds);
}

BSSL_CURL_SHIM_EXPORT int bsn_curl_multi_info_read(
    CURLM *multi_handle,
    int *msgs_in_queue,
    int *msg,
    CURL **easy_handle,
    CURLcode *result) {
    CURLMsg *message = curl_multi_info_read(multi_handle, msgs_in_queue);
    if (message == NULL) {
        return 0;
    }

    if (msg != NULL) {
        *msg = (int)message->msg;
    }

    if (easy_handle != NULL) {
        *easy_handle = message->easy_handle;
    }

    if (result != NULL) {
        *result = message->data.result;
    }

    return 1;
}

BSSL_CURL_SHIM_EXPORT CURLMcode bsn_curl_multi_wakeup(CURLM *multi_handle) {
    return curl_multi_wakeup(multi_handle);
}

BSSL_CURL_SHIM_EXPORT const char *bsn_curl_multi_strerror(CURLMcode code) {
    return curl_multi_strerror(code);
}
