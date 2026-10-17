#pragma once
#include <stdint.h>

namespace board {
constexpr int PwmPin = 3, SdaPin = 6, SclPin = 7, NtcPin = 0;
constexpr uint8_t LcdAddress = 0x27; // Change to 0x3F for a different PCF8574 backpack.
constexpr bool PwmActiveLow = true; // User's external NOT stage inverts the GPIO waveform.
// 18 Hz requires a sufficiently high LEDC resolution on ESP32-C3.
// 18 Hz / 8 bits fails at runtime: timer divider cannot reach that low frequency.
constexpr uint32_t PwmFrequencyHz = 18;
constexpr int PwmResolutionBits = 14;
static_assert(PwmResolutionBits >= 1 && PwmResolutionBits <= 14,
              "ESP32-C3 supports LEDC resolutions from 1 to 14 bits.");

// The configurable external curve, control policy and protocol keep 8-bit
// logical outputs (30..255). Convert only the inverted ELECTRICAL duty
// to the full LEDC timer range, preserving 0%=0 and 100%=all counts.
constexpr uint32_t LedcMaximumDuty = (1u << PwmResolutionBits) - 1u;
constexpr uint32_t LedcDutyFrom8Bit(uint32_t duty8) {
    return ((duty8 > 255u ? 255u : duty8) * LedcMaximumDuty + 127u) / 255u;
}
static_assert(LedcDutyFrom8Bit(0) == 0 && LedcDutyFrom8Bit(255) == LedcMaximumDuty,
              "8-bit -> LEDC duty conversion must preserve both endpoints.");
#ifndef VICTUS_ENABLE_PWM
#define VICTUS_ENABLE_PWM 0
#endif
constexpr bool EnablePwm = VICTUS_ENABLE_PWM != 0;
constexpr uint16_t Port = 8765;
constexpr char Hostname[] = "victus-base";
// Explicit device calibration, not a protocol limit. Revisit when Windows' logical scale changes.
constexpr int RequestMinimum = 0, RequestMaximum = 54, MinimumRunningPwm = 25;
constexpr float NtcNominalOhms = 10000, FixedResistorOhms = 10000, NtcBeta = 3950;
constexpr float SupplyMilliVolts = 3300;
}
