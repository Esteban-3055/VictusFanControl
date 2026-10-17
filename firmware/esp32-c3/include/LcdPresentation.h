#pragma once
// 16x2 presentation only. Does not own telemetry, the LCD bus or fan authority.
// Portable C++ so page timing and missing-data formatting can be regression tested.
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>

namespace victus {
enum class LcdPage : uint8_t { CpuGpu, RamDemand, FanStatus, Temperatures };

constexpr uint32_t LcdCpuGpuMs = 5000;
constexpr uint32_t LcdRamDemandMs = 5000;
constexpr uint32_t LcdFanStatusMs = 5000;
constexpr uint32_t LcdTemperaturesMs = 30000;
constexpr uint32_t LcdCycleMs =
    LcdCpuGpuMs + LcdRamDemandMs + LcdFanStatusMs + LcdTemperaturesMs;

inline LcdPage lcdPage(uint32_t elapsed) {
    auto offset = elapsed % LcdCycleMs;
    if (offset < LcdCpuGpuMs) return LcdPage::CpuGpu;
    if (offset < LcdCpuGpuMs + LcdRamDemandMs) return LcdPage::RamDemand;
    if (offset < LcdCpuGpuMs + LcdRamDemandMs + LcdFanStatusMs) return LcdPage::FanStatus;
    return LcdPage::Temperatures;
}
struct LcdReadings {
    double cpuPct = NAN, gpuPct = NAN, ramPct = NAN, demandPct = NAN;
    double cpuTempC = NAN, gpuTempC = NAN, ambientC = NAN;
    bool telemetryFresh = false, pwmEnabled = false, remote = false, wifi = false;
    int pwmDuty = -1; // 30..255 from control task; not measured RPM.
};
inline void lcdPad16(char (&row)[17]) {
    size_t length = std::strlen(row);
    while (length < 16) row[length++] = ' ';
    row[16] = '\0';
}
inline void lcdNumber(char (&buffer)[8], double value, double minimum, double maximum) {
    if (!std::isfinite(value) || value < minimum || value > maximum)
        std::snprintf(buffer, sizeof(buffer), "--");
    else
        std::snprintf(buffer, sizeof(buffer), "%.0f", value);
}
inline void renderLcd(LcdPage page, const LcdReadings& data,
                      uint32_t msRemainingOnTemperaturePage,
                      char (&first)[17], char (&second)[17]) {
    first[0] = '\0'; second[0] = '\0';
    char left[8]{}, right[8]{};
    switch (page) {
        case LcdPage::CpuGpu:
            lcdNumber(left, data.telemetryFresh ? data.cpuPct : NAN, 0, 100);
            lcdNumber(right, data.telemetryFresh ? data.gpuPct : NAN, 0, 100);
            std::snprintf(first, sizeof(first), "CPU: %s%%", left);
            std::snprintf(second, sizeof(second), "GPU: %s%%", right);
            break;
        case LcdPage::RamDemand:
            lcdNumber(left, data.telemetryFresh ? data.ramPct : NAN, 0, 100);
            lcdNumber(right, data.telemetryFresh ? data.demandPct : NAN, 0, 100);
            std::snprintf(first, sizeof(first), "RAM: %s%%", left);
            std::snprintf(second, sizeof(second), "DEMANDA: %s%%", right);
            break;
        case LcdPage::FanStatus:
            if (std::isfinite(data.ambientC)) {
                if (data.pwmEnabled)
                    std::snprintf(first, sizeof(first), "AMB:%.1fC P:%d", data.ambientC, data.pwmDuty);
                else std::snprintf(first, sizeof(first), "AMB:%.1fC PWM OFF", data.ambientC);
            } else std::snprintf(first, sizeof(first), "NTC Fault");
            std::snprintf(second, sizeof(second), "%s %s",
                          !data.pwmEnabled ? "PWM OFF" : (data.remote ? "REMOTE" : "LOCAL"),
                          data.wifi ? "WIFI" : "OFFLINE");
            break;
        case LcdPage::Temperatures:
            lcdNumber(left, data.telemetryFresh ? data.cpuTempC : NAN, -20, 130);
            lcdNumber(right, data.telemetryFresh ? data.gpuTempC : NAN, -20, 130);
            // Maximal CPU and GPU values both fit in 16 characters.
            if (std::snprintf(first, sizeof(first), "CPU:%sC GPU:%sC", left, right) > 16)
                std::snprintf(first, sizeof(first), "CPU:%sCGPU:%sC", left, right);
            if (std::isfinite(data.ambientC))
                std::snprintf(second, sizeof(second), "AMB:%.1fC %lus", data.ambientC,
                              static_cast<unsigned long>((msRemainingOnTemperaturePage + 999) / 1000));
            else std::snprintf(second, sizeof(second), "AMB:--  %lus",
                               static_cast<unsigned long>((msRemainingOnTemperaturePage + 999) / 1000));
            break;
    }
    lcdPad16(first); lcdPad16(second);
}
inline uint32_t lcdTemperatureRemaining(uint32_t elapsed) {
    uint32_t offset = elapsed % LcdCycleMs;
    const uint32_t start = LcdCpuGpuMs + LcdRamDemandMs + LcdFanStatusMs;
    return offset < start ? LcdTemperaturesMs : LcdCycleMs - offset;
}
} // namespace victus
