#include "VictusProtocol.h"
#include <set>
#include <ctype.h>

namespace victus {
namespace {
bool utf8(const std::string& s) {
    size_t i = 0;
    while (i < s.size()) {
        uint32_t c = static_cast<uint8_t>(s[i++]);
        if (c < 128) continue;
        int n; uint32_t min;
        if (c >= 0xC2 && c <= 0xDF) { n = 1; min = 0x80; c &= 31; }
        else if (c >= 0xE0 && c <= 0xEF) { n = 2; min = 0x800; c &= 15; }
        else if (c >= 0xF0 && c <= 0xF4) { n = 3; min = 0x10000; c &= 7; }
        else return false;
        while (n--) { if (i == s.size()) return false; auto b = static_cast<uint8_t>(s[i++]);
            if ((b & 0xC0) != 0x80) return false; c = (c << 6) | (b & 63); }
        if (c < min || c > 0x10FFFF || (c >= 0xD800 && c <= 0xDFFF)) return false;
    }
    return true;
}
struct Scanner {
    const std::string& s; size_t p = 0; int nodes = 0; std::string payload;
    explicit Scanner(const std::string& input) : s(input) {}
    void space() { while (p < s.size() && (s[p] == ' ' || s[p] == '\t' || s[p] == '\r' || s[p] == '\n')) ++p; }
    bool take(char c) { space(); if (p >= s.size() || s[p] != c) return false; ++p; return true; }
    bool text(std::string* decoded = nullptr) {
        space(); size_t start = p;
        if (p >= s.size() || s[p++] != '"') return false;
        while (p < s.size()) {
            auto c = static_cast<uint8_t>(s[p++]);
            if (c == '"') {
                if (decoded) {
                    StaticJsonDocument<512> key;
                    if (deserializeJson(key, s.substr(start, p - start)) || !key.is<const char*>()) return false;
                    auto str = key.as<JsonString>();
                    if (str.size() > 128 || str.size() != strlen(str.c_str())) return false;
                    *decoded = std::string(str.c_str(), str.size());
                }
                return true;
            }
            if (c < 32) return false;
            if (c == '\\') {
                if (p == s.size()) return false; char e = s[p++];
                if (e == 'u') {
                    if (p + 4 > s.size()) return false;
                    unsigned code = 0;
                    for (int j = 0; j < 4; ++j) { char h = s[p++]; if (!isxdigit(static_cast<unsigned char>(h))) return false;
                        code = code * 16 + (h <= '9' ? h - '0' : (h | 32) - 'a' + 10); }
                    if (code >= 0xD800 && code <= 0xDBFF) {
                        if (p + 6 > s.size() || s[p++] != '\\' || s[p++] != 'u') return false;
                        unsigned low = 0;
                        for (int j = 0; j < 4; ++j) { char h = s[p++]; if (!isxdigit(static_cast<unsigned char>(h))) return false;
                            low = low * 16 + (h <= '9' ? h - '0' : (h | 32) - 'a' + 10); }
                        if (low < 0xDC00 || low > 0xDFFF) return false;
                    } else if (code >= 0xDC00 && code <= 0xDFFF) return false;
                } else if (std::string("\"\\/bfnrt").find(e) == std::string::npos) return false;
            }
        }
        return false;
    }
    bool value(int depth) {
        if (depth > 8 || ++nodes > 256) return false;
        space(); if (p == s.size()) return false;
        if (s[p] == '"') return text();
        if (s[p] == '{') {
            ++p; std::set<std::string> keys; space(); if (p < s.size() && s[p] == '}') { ++p; return true; }
            do {
                std::string key; if (!text(&key) || !keys.insert(key).second || !take(':')) return false;
                space(); auto start = p; if (!value(depth + 1)) return false;
                if (depth == 1 && key == "payload") payload = s.substr(start, p - start);
                space(); if (p < s.size() && s[p] == '}') { ++p; return true; }
            } while (take(','));
            return false;
        }
        if (s[p] == '[') {
            ++p; space(); if (p < s.size() && s[p] == ']') { ++p; return true; }
            do { if (!value(depth + 1)) return false; space(); if (p < s.size() && s[p] == ']') { ++p; return true; } } while (take(','));
            return false;
        }
        for (const auto* literal : { "true", "false", "null" }) {
            size_t n = strlen(literal); if (s.compare(p, n, literal) == 0) { p += n; return true; }
        }
        if (s[p] == '-') ++p;
        if (p == s.size() || !isdigit(static_cast<unsigned char>(s[p]))) return false;
        if (s[p] == '0') ++p; else while (p < s.size() && isdigit(static_cast<unsigned char>(s[p]))) ++p;
        if (p < s.size() && s[p] == '.') {
            ++p; auto start = p; while (p < s.size() && isdigit(static_cast<unsigned char>(s[p]))) ++p; if (p == start) return false;
        }
        if (p < s.size() && (s[p] == 'e' || s[p] == 'E')) {
            ++p; if (p < s.size() && (s[p] == '+' || s[p] == '-')) ++p;
            auto start = p; while (p < s.size() && isdigit(static_cast<unsigned char>(s[p]))) ++p; if (p == start) return false;
        }
        return true;
    }
};
}
bool validateJson(const std::string& line, std::string& rawPayload) {
    if (line.empty() || line.size() > 4096 || !utf8(line)) return false;
    Scanner scanner{line}; scanner.space(); if (scanner.p == line.size() || line[scanner.p] != '{' || !scanner.value(1)) return false;
    scanner.space(); if (scanner.p != line.size() || scanner.payload.empty() || scanner.payload[0] != '{') return false;
    rawPayload = std::move(scanner.payload); return true;
}
}
