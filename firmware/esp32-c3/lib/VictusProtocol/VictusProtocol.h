#pragma once
#include <ArduinoJson.h>
#include <array>
#include <string>
#include <stdint.h>
#include <cmath>
#include "ExternalController.h"

namespace victus {
using Key = std::array<uint8_t, 32>;
struct Crypto {
    virtual ~Crypto() = default;
    virtual Key hmac(const Key&, const std::string&) = 0;
    virtual Key random() = 0;
};
std::string base64(const Key& bytes);
bool decodeKey(const std::string&, Key&);
bool equalProof(const std::string&, const std::string&);
// Strict JSON validation, duplicate-name rejection and exact payload-byte extraction.
bool validateJson(const std::string& line, std::string& rawPayload);
struct Measurement {
    double cpu = -1, gpu = -1, ram = -1, demand = -1;
    // Signed, validated telemetry from Windows, nullable when not available.
    // Never substitute 0 C for unavailable or expired thermal samples.
    double cpuTempC = NAN, gpuTempC = NAN;
    int request = -1;
    bool valid = false;
    std::string source = "Unknown";
    bool manual = false;
};
struct ControlPolicy {
    int minimum = 0, maximum = 54, minimumRunningPwm = 25;
    bool map(int request, int& pwm) const;
    static int local(double ambient);
    static int electricalDuty(int pct, bool inverted);
};
class Lease {
    ControlPolicy policy;
    uint32_t admittedAt = 0;
    int remotePwm = 0;
    bool active = false;
public:
    explicit Lease(ControlPolicy p = {}) : policy(p) {}
    void command(bool admitted, int request, uint32_t now) { active = admitted && policy.map(request,remotePwm); if (active) admittedAt = now; }
    void disable() { active = false; }
    int tick(uint32_t now, double ambient) {
        if (static_cast<uint32_t>(now-admittedAt)>=2000 || !std::isfinite(ambient) || ambient < -40 || ambient > 100) active=false;
        return active ? remotePwm : ControlPolicy::local(ambient);
    }
    bool remote() const { return active; }
};
class Session {
public:
    Session(const Key&, Crypto&);
    ~Session();
    bool accept(const std::string& line, uint32_t now, double ambient, int appliedPwm, bool remote,
        std::string& response);
    void sent(uint32_t now); // Called only after the complete response reached the TCP socket.
    bool authenticated() const { return phase == 2; }
    bool remoteRequest = false;
    Measurement latest;
    ExternalConfig configuration;
    bool hasConfiguration = false;
    std::string appliedConfigRevision;
    int appliedDuty = -1, externalRequest = -1;
private:
    Key pairing, receiveKey{}, sendKey{};
    Crypto& crypto;
    int phase = 0;
    int64_t previous = 0, outgoing = 0;
    std::string id, clientNonce, serverNonce;
    struct Stamp { int64_t sequence = 0; uint32_t at = 0; };
    std::array<Stamp, 16> replies{};
    int replyIndex = 0;
    std::string proof(const std::string& role);
    std::string frame(const std::string& type, int64_t seq, const std::string& payload, bool sign);
    bool telemetry(JsonObjectConst payload, uint32_t now);
};
}
