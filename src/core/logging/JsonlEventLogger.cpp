#include "JsonlEventLogger.h"

#include <array>
#include <chrono>
#include <cstdint>
#include <fstream>
#include <iomanip>
#include <sstream>
#include <string_view>
#include <vector>

namespace whereisit::core::logging {

namespace {
std::wstring UtcNowIso8601() {
    const auto now = std::chrono::system_clock::now();
    const auto t = std::chrono::system_clock::to_time_t(now);
    std::tm utc{};
#if defined(_WIN32)
    gmtime_s(&utc, &t);
#else
    gmtime_r(&t, &utc);
#endif

    std::wstringstream ss;
    ss << std::put_time(&utc, L"%Y-%m-%dT%H:%M:%SZ");
    return ss.str();
}

std::wstring UtcDateForFile() {
    const auto now = std::chrono::system_clock::now();
    const auto t = std::chrono::system_clock::to_time_t(now);
    std::tm utc{};
#if defined(_WIN32)
    gmtime_s(&utc, &t);
#else
    gmtime_r(&t, &utc);
#endif

    std::wstringstream file;
    file << std::put_time(&utc, L"%Y-%m-%d");
    return file.str();
}

std::string WStringToUtf8(std::wstring_view w) {
    std::string out;
    out.reserve(w.size());
    for (wchar_t ch : w) {
        if (ch >= 0 && ch <= 0x7F) {
            out.push_back(static_cast<char>(ch));
        } else {
            out.push_back('?');
        }
    }
    return out;
}

uint32_t RotR(uint32_t x, uint32_t n) {
    return (x >> n) | (x << (32 - n));
}

std::array<uint8_t, 32> Sha256(std::string_view data) {
    static constexpr uint32_t k[64] = {
        0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,
        0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,
        0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
        0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,
        0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,
        0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
        0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,
        0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2};
    uint32_t h[8] = {0x6a09e667,0xbb67ae85,0x3c6ef372,0xa54ff53a,0x510e527f,0x9b05688c,0x1f83d9ab,0x5be0cd19};
    std::vector<uint8_t> msg(data.begin(), data.end());
    const uint64_t bitLen = static_cast<uint64_t>(msg.size()) * 8ull;
    msg.push_back(0x80);
    while ((msg.size() % 64) != 56) msg.push_back(0x00);
    for (int i = 7; i >= 0; --i) msg.push_back(static_cast<uint8_t>((bitLen >> (8 * i)) & 0xFF));

    for (size_t chunk = 0; chunk < msg.size(); chunk += 64) {
        uint32_t w[64]{};
        for (size_t i = 0; i < 16; ++i) {
            const size_t j = chunk + (i * 4);
            w[i] = (static_cast<uint32_t>(msg[j]) << 24) | (static_cast<uint32_t>(msg[j + 1]) << 16) |
                   (static_cast<uint32_t>(msg[j + 2]) << 8) | static_cast<uint32_t>(msg[j + 3]);
        }
        for (size_t i = 16; i < 64; ++i) {
            const uint32_t s0 = RotR(w[i - 15], 7) ^ RotR(w[i - 15], 18) ^ (w[i - 15] >> 3);
            const uint32_t s1 = RotR(w[i - 2], 17) ^ RotR(w[i - 2], 19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16] + s0 + w[i - 7] + s1;
        }
        uint32_t a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
        for (size_t i = 0; i < 64; ++i) {
            const uint32_t S1 = RotR(e, 6) ^ RotR(e, 11) ^ RotR(e, 25);
            const uint32_t ch = (e & f) ^ ((~e) & g);
            const uint32_t temp1 = hh + S1 + ch + k[i] + w[i];
            const uint32_t S0 = RotR(a, 2) ^ RotR(a, 13) ^ RotR(a, 22);
            const uint32_t maj = (a & b) ^ (a & c) ^ (b & c);
            const uint32_t temp2 = S0 + maj;
            hh = g; g = f; f = e; e = d + temp1; d = c; c = b; b = a; a = temp1 + temp2;
        }
        h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e; h[5] += f; h[6] += g; h[7] += hh;
    }

    std::array<uint8_t, 32> out{};
    for (size_t i = 0; i < 8; ++i) {
        out[(i * 4)] = static_cast<uint8_t>((h[i] >> 24) & 0xFF);
        out[(i * 4) + 1] = static_cast<uint8_t>((h[i] >> 16) & 0xFF);
        out[(i * 4) + 2] = static_cast<uint8_t>((h[i] >> 8) & 0xFF);
        out[(i * 4) + 3] = static_cast<uint8_t>(h[i] & 0xFF);
    }
    return out;
}
} // namespace

JsonlEventLogger::JsonlEventLogger(std::filesystem::path baseDirectory)
    : baseDirectory_(std::move(baseDirectory)) {}

std::filesystem::path JsonlEventLogger::ResolveDailyPath() const {
    return baseDirectory_ / (UtcDateForFile() + L".jsonl");
}

void JsonlEventLogger::Log(const EventContext& ctx, std::optional<std::wstring> query) {
    std::filesystem::create_directories(baseDirectory_);
    const auto path = ResolveDailyPath();

    std::wstring queryHash;
    int queryTokens = 0;
    if (query.has_value()) {
        queryHash = Hash12(*query);
        bool inToken = false;
        for (wchar_t c : *query) {
            const bool space = (c == L' ' || c == L'\t' || c == L'\n');
            if (!space && !inToken) {
                inToken = true;
                ++queryTokens;
            }
            if (space) inToken = false;
        }
    }

    std::wstringstream line;
    line << L"{\"ts\":\"" << Escape(UtcNowIso8601())
         << L"\",\"lvl\":\"" << Escape(ctx.level)
         << L"\",\"event\":\"" << Escape(ctx.event)
         << L"\",\"module\":\"" << Escape(ctx.module)
         << L"\",\"corr\":\"" << Escape(ctx.corr) << L"\"";

    if (ctx.op) line << L",\"op\":\"" << Escape(*ctx.op) << L"\"";
    if (ctx.bytes) line << L",\"bytes\":" << *ctx.bytes;
    if (ctx.durMs) line << L",\"dur_ms\":" << *ctx.durMs;
    if (ctx.clientPid) line << L",\"client_pid\":" << *ctx.clientPid;
    if (!queryHash.empty()) {
        line << L",\"query\":{\"token_count\":" << queryTokens
             << L",\"hash12\":\"" << queryHash << L"\"}";
    }
    line << L"}";

    std::wstring outLine = line.str();
    if (outLine.size() > 4096) {
        outLine = L"{\"ts\":\"" + Escape(UtcNowIso8601()) + L"\",\"lvl\":\"warn\",\"event\":\"logging.line_omitted\",\"module\":\"logging\",\"corr\":\"" + Escape(ctx.corr) + L"\"}";
    }

    std::wofstream out(path, std::ios::app);
    if (!out) return;
    out << outLine << L"\n";
}

void JsonlEventLogger::PurgeOlderThanDays(int retentionDays) {
    if (retentionDays <= 0 || !std::filesystem::exists(baseDirectory_)) return;

    const auto cutoff = std::filesystem::file_time_type::clock::now() - std::chrono::hours(24 * retentionDays);
    for (const auto& ent : std::filesystem::directory_iterator(baseDirectory_)) {
        if (!ent.is_regular_file()) continue;
        std::error_code ec;
        const auto wt = ent.last_write_time(ec);
        if (ec) continue;
        if (wt < cutoff) {
            std::filesystem::remove(ent.path(), ec);
        }
    }
}

std::wstring JsonlEventLogger::Hash12(std::wstring_view text) {
    const auto digest = Sha256(WStringToUtf8(text));
    std::wstringstream ss;
    ss << std::hex << std::setfill(L'0');
    for (size_t i = 0; i < 6; ++i) {
        ss << std::setw(2) << static_cast<unsigned>(digest[i]);
    }
    return ss.str();
}

std::wstring JsonlEventLogger::Escape(std::wstring_view value) {
    std::wstring out;
    for (wchar_t ch : value) {
        if (ch == L'\\') out += L"\\\\";
        else if (ch == L'"') out += L"\\\"";
        else if (ch == L'\n') out += L"\\n";
        else if (ch == L'\r') out += L"\\r";
        else if (ch == L'\t') out += L"\\t";
        else out += ch;
    }
    return out;
}

} // namespace whereisit::core::logging
