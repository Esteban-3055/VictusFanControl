#pragma once
#include <ArduinoJson.h>
#include <array>
#include <string>
#include <stdint.h>
#include <cmath>
#include "ExternalController.h"
#include "HyperXDisplay.h"

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
    double hyperxPct = NAN;
    uint32_t hyperxAge = 180000;
    int64_t hyperxSampledUtc=0;
    double freshHyperx(uint32_t now) const {
        return linkFresh(now) && hyperxAge<180000 && static_cast<uint32_t>(now-receivedAt)<180000-hyperxAge ? hyperxPct : NAN;
    }
    int request = -1;
    bool valid = false;
    std::string source = "Unknown";
    bool manual = false;
    // Source age at capture plus monotonic residence on the ESP32. Missing
    // timestamps never become fresh merely because a signed frame arrived.
    uint32_t receivedAt = 0;
    bool received = false;
    uint32_t usageAge = 5000, ramAge = 5000, demandAge = 5000;
    uint32_t cpuTempAge = 5000, gpuTempAge = 5000;
    bool linkFresh(uint32_t now) const { return received && static_cast<uint32_t>(now-receivedAt)<2000; }
    double fresh(double value, uint32_t sourceAge, uint32_t now) const {
        return linkFresh(now) && sourceAge<5000 && static_cast<uint32_t>(now-receivedAt)<5000-sourceAge ? value : NAN;
    }
};
class Session {
public:
    Session(const Key&, Crypto&, const ExternalConfig* previousConfiguration = nullptr);
    ~Session();
    bool accept(const std::string& line, uint32_t now, double ambient, int appliedPwm, bool remote,
        std::string& response);
    void sent(uint32_t now); // Called only after the complete response reached the TCP socket.
    bool authenticated() const { return phase == 2; }
    bool remoteRequest = false;
    Measurement latest;
    ExternalConfig configuration;
    bool hasConfiguration = false;
    HyperXDisplayConfig hyperxDisplay;
    bool hasHyperxDisplay=false;
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
