/*
 * RomboTool - Combo Filter Engine
 * Pure C implementation for maximum performance
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <stdint.h>

#ifdef _WIN32
    #define EXPORT __declspec(dllexport)
#else
    #define EXPORT __attribute__((visibility("default")))
#endif

#define MAX_LINE 4096
#define MAX_FIELD 256
#define MIN_PASS 4
#define MAX_PASS 128
#define HASH_SIZE 1000003

typedef struct {
    int64_t total, valid, emails, usernames, phones, duplicates, invalid;
} Stats;

typedef struct {
    char user[MAX_FIELD];
    char pass[MAX_FIELD];
    int type;
} Combo;

static uint64_t* htable = NULL;

static char* trim(char* s) {
    if (!s) return s;
    while (isspace((unsigned char)*s)) s++;
    if (!*s) return s;
    char* e = s + strlen(s) - 1;
    while (e > s && isspace((unsigned char)*e)) e--;
    e[1] = '\0';
    return s;
}

static int is_email(const char* s) {
    if (!s || strlen(s) < 5) return 0;
    const char* at = strchr(s, '@');
    if (!at || at == s) return 0;
    const char* dot = strrchr(at, '.');
    if (!dot || dot == at + 1 || !dot[1]) return 0;
    int ext = strlen(dot + 1);
    return ext >= 2 && ext <= 10 && !strchr(s, ' ');
}

static int is_phone(const char* s) {
    if (!s) return 0;
    int digits = 0, len = strlen(s);
    if (len < 8 || len > 20) return 0;
    for (int i = 0; s[i]; i++) {
        if (isdigit((unsigned char)s[i])) digits++;
        else if (s[i] != '+' && s[i] != '-' && s[i] != ' ' && s[i] != '(' && s[i] != ')') return 0;
    }
    return digits >= 8 && digits <= 15;
}

static int is_garbage(const char* s) {
    if (!s) return 0;
    return strstr(s, "http://") || strstr(s, "https://") || strstr(s, "www.") ||
           strstr(s, ".com/") || strstr(s, ".net/") || strstr(s, ".org/") ||
           strstr(s, "/auth/") || strstr(s, "/login") || strstr(s, "/signup");
}

static int valid_pass(const char* s) {
    if (!s) return 0;
    int len = strlen(s);
    if (len < MIN_PASS || len > MAX_PASS || is_garbage(s)) return 0;
    if (!strcmp(s, "https") || !strcmp(s, "http")) return 0;
    if (len >= 5 && !strcmp(s + len - 5, "https")) return 0;
    if (strstr(s, "t.me/") && (strstr(s, "buy") || strstr(s, "Cloud"))) return 0;
    if (len >= 4 && (!strcmp(s + len - 4, ".com") || !strcmp(s + len - 4, ".net") || !strcmp(s + len - 4, ".org"))) return 0;
    if (strchr(s, ' ')) {
        int d = 0;
        for (int i = 0; s[i]; i++) if (isdigit((unsigned char)s[i])) d++;
        if (d >= 8 || strstr(s, "buy") || strstr(s, "You can") || strstr(s, "http")) return 0;
    }
    if (strstr(s, " URL") || strstr(s, "ID ") || strstr(s, "MA ")) return 0;
    int alnum = 0, alpha = 0;
    for (int i = 0; s[i]; i++) {
        if (isalnum((unsigned char)s[i])) alnum = 1;
        if (isalpha((unsigned char)s[i])) alpha = 1;
    }
    return alnum && (alpha || len >= 6);
}

static int valid_user(const char* s) {
    if (!s) return 0;
    int len = strlen(s);
    if (len < 2 || len > 100 || is_garbage(s)) return 0;
    if (!strcmp(s, "https") || !strcmp(s, "http") || strstr(s, "//") || strstr(s, "http")) return 0;
    if (strstr(s, "t.me/") || strchr(s, ';')) return 0;
    if (len <= 4) {
        int all = 1;
        for (int i = 0; s[i]; i++) if (!isdigit((unsigned char)s[i])) all = 0;
        if (all) return 0;
    }
    return 1;
}

static int split(const char* line, char p[][MAX_FIELD], int max) {
    int n = 0;
    char buf[MAX_LINE];
    strncpy(buf, line, MAX_LINE - 1);
    buf[MAX_LINE - 1] = '\0';
    for (int i = 0; buf[i]; i++) if (buf[i] == '|') buf[i] = ':';
    char* t = strtok(buf, ":");
    while (t && n < max) {
        char* tr = trim(t);
        if (tr && *tr) { strncpy(p[n], tr, MAX_FIELD - 1); p[n][MAX_FIELD - 1] = '\0'; n++; }
        t = strtok(NULL, ":");
    }
    return n;
}

EXPORT int parse_combo(const char* input, Combo* r) {
    if (!input || !r) return 0;
    char line[MAX_LINE];
    strncpy(line, input, MAX_LINE - 1);
    line[MAX_LINE - 1] = '\0';
    char* s = trim(line);
    if (!s || strlen(s) < 5 || s[0] == '#') return 0;
    if (strstr(s, "@kingulp") || strstr(s, "t.me/+") || strstr(s, "MonkeyBase") || strstr(s, "You can buy")) return 0;
    if (s[0] == '/' && s[1] == '/') return 0;
    if (strstr(s, "Browser/") || strstr(s, "Chrome_") || strstr(s, ".txt:") || strchr(s, ';')) return 0;

    char p[20][MAX_FIELD] = {0};
    int n = split(s, p, 20);
    if (n < 2) return 0;

    for (int i = 0; i < n - 1; i++) {
        if (is_email(p[i]) && valid_pass(p[i + 1])) {
            strncpy(r->user, p[i], MAX_FIELD - 1);
            strncpy(r->pass, p[i + 1], MAX_FIELD - 1);
            r->type = 1;
            return 1;
        }
    }
    for (int i = 0; i < n - 1; i++) {
        if (is_phone(p[i]) && !is_garbage(p[i]) && valid_pass(p[i + 1])) {
            strncpy(r->user, p[i], MAX_FIELD - 1);
            strncpy(r->pass, p[i + 1], MAX_FIELD - 1);
            r->type = 2;
            return 1;
        }
    }
    int start = 0;
    for (int i = 0; i < n; i++) {
        if (strstr(p[i], "http") || strstr(p[i], "www") || strstr(p[i], ".com") ||
            strstr(p[i], ".net") || strstr(p[i], ".org") || strstr(p[i], "/auth") ||
            strstr(p[i], "/login") || strstr(p[i], "/signup") || strstr(p[i], "realms")) {
            start = i + 1;
        } else break;
    }
    if (start < n - 1) {
        char* u = p[start], *pw = p[start + 1];
        if (start + 2 < n && !valid_pass(pw) && valid_pass(p[start + 2])) { u = p[start + 1]; pw = p[start + 2]; }
        if (valid_user(u) && valid_pass(pw)) {
            strncpy(r->user, u, MAX_FIELD - 1);
            strncpy(r->pass, pw, MAX_FIELD - 1);
            r->type = is_email(u) ? 1 : (is_phone(u) ? 2 : 0);
            return 1;
        }
    }
    for (int i = n - 1; i >= 1; i--) {
        if (valid_pass(p[i])) {
            for (int j = i - 1; j >= 0; j--) {
                if (valid_user(p[j]) && !is_garbage(p[j])) {
                    strncpy(r->user, p[j], MAX_FIELD - 1);
                    strncpy(r->pass, p[i], MAX_FIELD - 1);
                    r->type = is_email(p[j]) ? 1 : (is_phone(p[j]) ? 2 : 0);
                    return 1;
                }
            }
        }
    }
    return 0;
}

static uint64_t hash(const char* u, const char* p) {
    uint64_t h = 5381;
    for (int i = 0; u[i]; i++) h = ((h << 5) + h) + (unsigned char)tolower(u[i]);
    h = ((h << 5) + h) + ':';
    for (int i = 0; p[i]; i++) h = ((h << 5) + h) + (unsigned char)p[i];
    return h;
}

static void init_htable(void) { if (!htable) htable = (uint64_t*)calloc(HASH_SIZE, sizeof(uint64_t)); }
static void free_htable(void) { if (htable) { free(htable); htable = NULL; } }

static int is_dup(const char* u, const char* p) {
    if (!htable) init_htable();
    uint64_t h = hash(u, p), idx = h % HASH_SIZE;
    for (int i = 0; i < 100; i++) {
        uint64_t ci = (idx + i) % HASH_SIZE;
        if (htable[ci] == 0) { htable[ci] = h; return 0; }
        if (htable[ci] == h) return 1;
    }
    return 0;
}

EXPORT int process_file(const char* in, const char* out, Stats* st, int dedup) {
    FILE* fi = fopen(in, "r");
    if (!fi) return -1;
    FILE* fo = fopen(out, "w");
    if (!fo) { fclose(fi); return -2; }
    if (dedup) init_htable();
    memset(st, 0, sizeof(Stats));
    char line[MAX_LINE];
    Combo c;
    while (fgets(line, MAX_LINE, fi)) {
        st->total++;
        memset(&c, 0, sizeof(c));
        if (parse_combo(line, &c)) {
            if (dedup && is_dup(c.user, c.pass)) { st->duplicates++; continue; }
            fprintf(fo, "%s:%s\n", c.user, c.pass);
            st->valid++;
            if (c.type == 1) st->emails++; else if (c.type == 2) st->phones++; else st->usernames++;
        } else st->invalid++;
    }
    if (dedup) free_htable();
    fclose(fi); fclose(fo);
    return 0;
}

#ifndef BUILD_DLL
int main(int argc, char** argv) {
    printf("\n  ██████╗  ██████╗ ███╗   ███╗██████╗  ██████╗ ████████╗ ██████╗  ██████╗ ██╗     \n");
    printf("  ██╔══██╗██╔═══██╗████╗ ████║██╔══██╗██╔═══██╗╚══██╔══╝██╔═══██╗██╔═══██╗██║     \n");
    printf("  ██████╔╝██║   ██║██╔████╔██║██████╔╝██║   ██║   ██║   ██║   ██║██║   ██║██║     \n");
    printf("  ██╔══██╗██║   ██║██║╚██╔╝██║██╔══██╗██║   ██║   ██║   ██║   ██║██║   ██║██║     \n");
    printf("  ██║  ██║╚██████╔╝██║ ╚═╝ ██║██████╔╝╚██████╔╝   ██║   ╚██████╔╝╚██████╔╝███████╗\n");
    printf("  ╚═╝  ╚═╝ ╚═════╝ ╚═╝     ╚═╝╚═════╝  ╚═════╝    ╚═╝    ╚═════╝  ╚═════╝ ╚══════╝\n");
    printf("                              v1.0\n\n");
    if (argc < 3) { printf("Usage: %s <input> <output> [-d]\n  -d  Remove duplicates\n", argv[0]); return 1; }
    int dedup = argc >= 4 && !strcmp(argv[3], "-d");
    printf("[*] Input:  %s\n[*] Output: %s\n[*] Dedup:  %s\n\n", argv[1], argv[2], dedup ? "ON" : "OFF");
    Stats st;
    if (process_file(argv[1], argv[2], &st, dedup) < 0) { printf("[!] Error opening files\n"); return 1; }
    printf("═══════════════════════════════════════════════════════════════════════════════════\n");
    printf("  Total: %lld | Valid: %lld | Emails: %lld | Users: %lld | Phones: %lld\n",
           (long long)st.total, (long long)st.valid, (long long)st.emails, (long long)st.usernames, (long long)st.phones);
    printf("  Duplicates: %lld | Invalid: %lld | Rate: %.1f%%\n",
           (long long)st.duplicates, (long long)st.invalid, st.total > 0 ? 100.0 * st.valid / st.total : 0);
    printf("═══════════════════════════════════════════════════════════════════════════════════\n");
    printf("[+] Saved to: %s\n\n", argv[2]);
    return 0;
}
#endif
