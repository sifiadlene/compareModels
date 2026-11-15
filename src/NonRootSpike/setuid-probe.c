#include <stdio.h>
#include <unistd.h>

int main(void)
{
    printf("ruid=%lu euid=%lu\n", (unsigned long)getuid(), (unsigned long)geteuid());
    if (geteuid() == 0 || setuid(0) == 0)
        return 0;
    return 1;
}
