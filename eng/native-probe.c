#include <dlfcn.h>
#include <stdio.h>
int main(int argc, char **argv) {
    void *library = dlopen(argc > 1 ? argv[1] : "./libminirpcore.so", RTLD_NOW);
    if (!library) { fprintf(stderr, "%s\n", dlerror()); return 1; }
    int (*version)(void) = (int (*)(void))dlsym(library, "mc_version");
    if (!version) { fprintf(stderr, "%s\n", dlerror()); return 2; }
    printf("NativeAOT core ABI: %d\n", version());
    return 0;
}
