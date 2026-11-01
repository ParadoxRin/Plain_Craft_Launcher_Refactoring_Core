/* Unix companion: exec keeps the owned game PID; the logger survives the Host.
 * Secrets arrive through stdin, never argv, environment, or a staging file.
 * stdout is only a completion pipe; no game output is sent to the Host. */
#define _POSIX_C_SOURCE 200809L
#define _DARWIN_C_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <stdint.h>
#include <sys/stat.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#define LIMIT (16 * 1024 * 1024)
#define CONFIG_LIMIT (1024 * 1024)
static const char marker[] = "<game log truncated at 16 MiB>\n";
struct secret { unsigned char *data; uint32_t size; };
static int read_all(int fd, void *data, size_t n) {
    unsigned char *p = data;
    while (n) {
        ssize_t k = read(fd, p, n);
        if (k < 0 && errno == EINTR) continue;
        if (k <= 0) return -1;
        p += k; n -= (size_t)k;
    }
    return 0;
}
static int write_all(int fd, const void *data, size_t n) {
    const unsigned char *p = data;
    while (n) {
        ssize_t k = write(fd, p, n);
        if (k < 0 && errno == EINTR) continue;
        if (k <= 0) return -1;
        p += k; n -= (size_t)k;
    }
    return 0;
}
static int number(uint32_t *value) {
    unsigned char b[4];
    if (read_all(0, b, 4)) return -1;
    *value = (uint32_t)b[0] | (uint32_t)b[1]<<8 | (uint32_t)b[2]<<16 | (uint32_t)b[3]<<24;
    return 0;
}
static char *self;
static void cleanup(void) {
    /* Only the uniquely extracted executable and its now-empty directory. */
    if (!self) return;
    unlink(self);
    char *slash = strrchr(self, '/');
    if (slash) { *slash = 0; rmdir(self); }
}
static size_t written;
static int clipped;
static void emit(int fd, const void *data, size_t n) {
    if (clipped) return;
    if (written + n > LIMIT - (sizeof(marker)-1)) {
        (void)write_all(fd, marker, sizeof(marker)-1);
        clipped = 1;
    } else if (write_all(fd, data, n)) clipped = 1;
    else written += n;
}
static void logger(int input, int file, struct secret *secrets, uint32_t count, size_t longest) {
    /* Keep enough lookahead to match secrets across reads and newlines. */
    size_t capacity = longest + 8192, used = 0;
    unsigned char *buffer = malloc(capacity);
    if (!buffer) _exit(125);
    int eof = 0;
    while (!eof) {
        ssize_t n = read(input, buffer + used, capacity - used);
        if (n < 0 && errno == EINTR) continue;
        if (n <= 0) eof = 1; else used += (size_t)n;
        size_t pos = 0, plain = 0;
        while (pos < used && (eof || used - pos >= longest)) {
            size_t match = 0;
            for (uint32_t i = 0; i < count; ++i)
                if (secrets[i].size > match && secrets[i].size <= used-pos &&
                    !memcmp(buffer+pos, secrets[i].data, secrets[i].size)) match = secrets[i].size;
            if (match) {
                emit(file, buffer+plain, pos-plain);
                emit(file, "<redacted>", 10);
                pos += match; plain = pos;
            } else ++pos;
        }
        emit(file, buffer+plain, pos-plain);
        used -= pos;
        memmove(buffer, buffer+pos, used);
    }
    close(file); close(input); cleanup(); close(1);
    _exit(0);
}
static int run(int argc, char **argv) {
    if (argc < 3) return 125;
    uint32_t count;
    if (number(&count) || count > 4096) return 125;
    struct secret *secrets = calloc(count ? count : 1, sizeof(*secrets));
    if (!secrets) return 125;
    size_t total = 0, longest = 1;
    for (uint32_t i = 0; i < count; ++i) {
        if (number(&secrets[i].size) || !secrets[i].size) return 125;
        total += secrets[i].size;
        if (total > CONFIG_LIMIT) return 125;
        secrets[i].data = malloc(secrets[i].size);
        if (!secrets[i].data || read_all(0, secrets[i].data, secrets[i].size)) return 125;
        if (secrets[i].size > longest) longest = secrets[i].size;
    }
    int file = open(argv[1], O_WRONLY|O_CREAT|O_NONBLOCK|O_NOFOLLOW, 0600);
    int pipes[2];
    struct stat status;
    if (file < 0 || fstat(file, &status) || !S_ISREG(status.st_mode) ||
        fchmod(file, 0600) || ftruncate(file, 0) || pipe(pipes)) return 125;
    signal(SIGHUP, SIG_IGN);
    signal(SIGPIPE, SIG_IGN);
    pid_t child = fork();
    if (child < 0) return 125;
    if (!child) {
        close(pipes[1]); close(0); close(2);
        logger(pipes[0], file, secrets, count, longest);
    }
    close(file); close(pipes[0]);
    for (uint32_t i = 0; i < count; ++i) {
        memset(secrets[i].data, 0, secrets[i].size); free(secrets[i].data);
    }
    free(secrets);
    int null = open("/dev/null", O_RDONLY);
    if (null < 0 || dup2(null, 0) < 0 || dup2(pipes[1], 1) < 0 || dup2(pipes[1], 2) < 0) return 125;
    if (null > 2) close(null);
    close(pipes[1]);
    signal(SIGPIPE, SIG_DFL);
    execvp(argv[2], argv+2);
    return 127;
}

int main(int argc, char **argv) {
    self = strdup(argv[0]);
    int result = run(argc, argv);
    cleanup();
    return result;
}
