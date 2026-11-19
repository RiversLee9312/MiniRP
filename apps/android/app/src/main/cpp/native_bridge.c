#include <jni.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <dlfcn.h>

/* JNI is deliberately a byte transport. Java modified UTF-8 would corrupt emoji. */
static int64_t (*core_create)(const char *, int);
static char *(*core_dispatch)(int64_t, const char *);
static char *(*core_poll)(int64_t);
static void (*core_release)(void *);
static void (*core_destroy)(int64_t);
static void *(*core_binary_take)(int64_t, const char *, int *);
static int (*core_binary_write)(int64_t, const char *, const void *, int);

JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM *vm, void *reserved) {
    (void)vm; (void)reserved;
    void *library = dlopen("libminichatcore.so", RTLD_NOW | RTLD_LOCAL);
    if (!library) return JNI_ERR;
    core_create = dlsym(library, "mc_create_with_options");
    core_dispatch = dlsym(library, "mc_dispatch");
    core_poll = dlsym(library, "mc_poll");
    core_release = dlsym(library, "mc_release");
    core_destroy = dlsym(library, "mc_destroy");
    core_binary_take = dlsym(library, "mc_binary_take");
    core_binary_write = dlsym(library, "mc_binary_write");
    if (!core_create || !core_dispatch || !core_poll || !core_release || !core_destroy ||
        !core_binary_take || !core_binary_write) return JNI_ERR;
    return JNI_VERSION_1_6;
}

static char *input(JNIEnv *env, jbyteArray bytes) {
    jsize length = (*env)->GetArrayLength(env, bytes);
    char *text = malloc((size_t)length + 1);
    if (!text) return NULL;
    (*env)->GetByteArrayRegion(env, bytes, 0, length, (jbyte *)text);
    text[length] = 0;
    return text;
}
static jbyteArray output(JNIEnv *env, char *text) {
    if (!text) return NULL;
    jsize length = (jsize)strlen(text);
    jbyteArray bytes = (*env)->NewByteArray(env, length);
    if (bytes) (*env)->SetByteArrayRegion(env, bytes, 0, length, (const jbyte *)text);
    core_release(text);
    return bytes;
}
JNIEXPORT jlong JNICALL Java_com_minichat_app_core_NativeBridge_create(JNIEnv *env, jobject self, jbyteArray root, jboolean seed_default_preset) {
    (void)self; char *path = input(env, root); if (!path) return 0;
    int64_t handle = core_create(path, seed_default_preset); free(path); return handle;
}
JNIEXPORT jbyteArray JNICALL Java_com_minichat_app_core_NativeBridge_dispatch(JNIEnv *env, jobject self, jlong handle, jbyteArray json) {
    (void)self; char *text = input(env, json); if (!text) return NULL;
    char *result = core_dispatch(handle, text); free(text); return output(env, result);
}
JNIEXPORT jbyteArray JNICALL Java_com_minichat_app_core_NativeBridge_poll(JNIEnv *env, jobject self, jlong handle) {
    (void)self; return output(env, core_poll(handle));
}
JNIEXPORT void JNICALL Java_com_minichat_app_core_NativeBridge_destroy(JNIEnv *env, jobject self, jlong handle) {
    (void)env; (void)self; core_destroy(handle);
}
JNIEXPORT jbyteArray JNICALL Java_com_minichat_app_core_NativeBridge_takeBinary(JNIEnv *env, jobject self, jlong handle, jbyteArray nonce) {
    (void)self;
    char *token = input(env, nonce);
    if (!token) return NULL;
    int length = 0;
    void *buffer = core_binary_take(handle, token, &length);
    free(token);
    if (!buffer) return NULL;
    jbyteArray bytes = (*env)->NewByteArray(env, length);
    if (bytes) (*env)->SetByteArrayRegion(env, bytes, 0, length, buffer);
    core_release(buffer);
    return bytes;
}
JNIEXPORT jboolean JNICALL Java_com_minichat_app_core_NativeBridge_writeBinary(JNIEnv *env, jobject self, jlong handle, jbyteArray nonce, jbyteArray value) {
    (void)self;
    char *token = input(env, nonce);
    if (!token) return JNI_FALSE;
    jbyte *buffer = (*env)->GetByteArrayElements(env, value, NULL);
    jboolean result = JNI_FALSE;
    if (buffer) {
        result = core_binary_write(handle, token, buffer, (*env)->GetArrayLength(env, value)) ? JNI_TRUE : JNI_FALSE;
        (*env)->ReleaseByteArrayElements(env, value, buffer, JNI_ABORT);
    }
    free(token);
    return result;
}
