#pragma once
#include <stdint.h>

namespace board {
constexpr int PwmPin = 3, SdaPin = 6, SclPin = 7, NtcPin = 0;
constexpr uint8_t LcdAddress = 0x27; // Change to 0x3F for a different PCF8574 backpack.
constexpr bool PwmActiveLow = true; // User's external NOT stage inverts the GPIO waveform.
constexpr uint32_t PwmFrequencyHz = 25000;
constexpr int PwmResolutionBits = 8;
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
