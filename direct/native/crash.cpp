#include <cstdio>

extern "C" __attribute__((visibility("default"))) void trigger_segfault()
{
    std::fprintf(stderr, "Native library is intentionally dereferencing null.\n");
    std::fflush(stderr);

    volatile int* invalid = nullptr;
    *invalid = 42;
}
