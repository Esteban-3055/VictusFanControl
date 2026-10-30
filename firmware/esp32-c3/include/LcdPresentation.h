#pragma once
// Portable 16x2 presentation. No bus access or fan authority.
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>

namespace victus {
enum class LcdPage : uint8_t { Temperatures, Usage, Demand, Connection };
constexpr uint32_t LcdTemperaturesMs = 30000;
constexpr uint32_t LcdUsageMs = 5000, LcdDemandMs = 5000, LcdConnectionMs = 5000;
constexpr uint32_t LcdCycleMs = LcdTemperaturesMs + LcdUsageMs + LcdDemandMs + LcdConnectionMs;
inline LcdPage lcdPage(uint32_t elapsed) {
    auto offset = elapsed % LcdCycleMs;
    if (offset < LcdTemperaturesMs) return LcdPage::Temperatures;
    if (offset < LcdTemperaturesMs + LcdUsageMs) return LcdPage::Usage;
    if (offset < LcdTemperaturesMs + LcdUsageMs + LcdDemandMs) return LcdPage::Demand;
    return LcdPage::Connection;
}
// Advance by complete cycles: preserves the page across millis() rollover.
// I2C recovery never restarts this clock.
inline uint32_t lcdElapsed(uint32_t now, uint32_t& cycleStartedAt) {
    uint32_t elapsed = now - cycleStartedAt;
    cycleStartedAt += (elapsed / LcdCycleMs) * LcdCycleMs;
    return elapsed % LcdCycleMs;
}
struct LcdReadings {
    double cpuPct = NAN, gpuPct = NAN, ramPct = NAN, demandPct = NAN;
    double cpuTempC = NAN, gpuTempC = NAN, ambientC = NAN, hyperxPct = NAN;
    bool telemetryFresh = false, pcAuthenticated = false, pwmEnabled = false, wifi = false;
    bool hyperxCached=false;
    int pwmDuty = -1; // Logical applied command, not RPM or proof of rotation.
};
inline void lcdPad16(char (&row)[17]) {
    size_t length = std::strlen(row);
    while (length < 16) row[length++] = ' ';
    row[16] = '\0';
}
inline void lcdNumber(char (&buffer)[8], double value, double minimum, double maximum) {
    if (!std::isfinite(value) || value < minimum || value > maximum)
        std::snprintf(buffer, sizeof(buffer), "--");
    else std::snprintf(buffer, sizeof(buffer), "%.0f", value);
}
inline void renderLcd(LcdPage page, const LcdReadings& data,
                      char (&first)[17], char (&second)[17]) {
    first[0] = '\0'; second[0] = '\0';
    char left[8]{}, right[8]{};
    // Ambient freshness is established by currentAmbient(), independently of PC telemetry.
    const bool ambientValid = std::isfinite(data.ambientC) && data.ambientC >= -40 && data.ambientC <= 100;
    switch (page) {
        case LcdPage::Temperatures:
            lcdNumber(left, data.telemetryFresh ? data.cpuTempC : NAN, -20, 130);
            lcdNumber(right, data.telemetryFresh ? data.gpuTempC : NAN, -20, 130);
            if (std::snprintf(first, sizeof(first), "CPU:%sC GPU:%sC", left, right) > 16)
                std::snprintf(first, sizeof(first), "CPU:%sCGPU:%sC", left, right);
            // CPU/GPU retain their independent source ages; HyperX has its own 180 s age.
            lcdNumber(left, ambientValid ? data.ambientC : NAN, -40, 100);
            lcdNumber(right, (data.telemetryFresh || data.hyperxCached) ? data.hyperxPct : NAN, 0, 100);
            if(std::snprintf(second, sizeof(second), "Amb:%sC Hyp:%s%c", left, right, data.hyperxCached?'*':'%')>16)
                std::snprintf(second, sizeof(second), "Amb:%sCHyp:%s%c", left, right, data.hyperxCached?'*':'%');
            break;
        case LcdPage::Usage:
            lcdNumber(left, data.telemetryFresh ? data.cpuPct : NAN, 0, 100);
            lcdNumber(right, data.telemetryFresh ? data.gpuPct : NAN, 0, 100);
            std::snprintf(first, sizeof(first), "CPU:%3s%%GPU:%3s%%", left, right);
            lcdNumber(left, data.telemetryFresh ? data.ramPct : NAN, 0, 100);
            std::snprintf(second, sizeof(second), "RAM: %s%%", left);
            break;
        case LcdPage::Demand:
            lcdNumber(left, data.telemetryFresh ? data.demandPct : NAN, 0, 100);
            std::snprintf(first, sizeof(first), "DEMANDA: %s%%", left);
            if (data.pwmEnabled && data.pwmDuty >= 0 && data.pwmDuty <= 255)
                std::snprintf(second, sizeof(second), "PWM:%3d%% (%d)",
                              static_cast<int>(std::lround(data.pwmDuty * 100.0 / 255)), data.pwmDuty);
            else std::snprintf(second, sizeof(second), "PWM:-- (OFF)");
            break;
        case LcdPage::Connection:
            std::snprintf(first, sizeof(first), "PC: %s", !data.pcAuthenticated ? "desconectado" :
                          (data.telemetryFresh ? "conectado" : "sin datos"));
            std::snprintf(second, sizeof(second), "WiFi: %s", data.wifi ? "conectado" : "sin red");
            break;
    }
    lcdPad16(first); lcdPad16(second);
}
} // namespace victus
