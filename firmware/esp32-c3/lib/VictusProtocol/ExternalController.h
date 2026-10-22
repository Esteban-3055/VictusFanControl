#pragma once
#include <array>
#include <algorithm>
#include <cmath>
#include <cstdint>

namespace victus {
struct CurvePoint { int input=0, output=30; };
// POD: safe to copy through the one-slot FreeRTOS queue. Logical fan duty, before the NOT stage.
struct ExternalConfig {
    char revision[33]{};
    int count=5;
    std::array<CurvePoint,16> points{{{0,30},{25,70},{50,130},{75,195},{100,255}}};
    int input=0, requestMinimum=0, requestMaximum=54, manualOutput=130;
    int rise=12, fall=6, filterMs=1500, hysteresis=3, downHoldMs=3000, startMs=800, startOutput=255;
    bool valid() const;
    double evaluate(double x) const;
};
class ExternalController {
    ExternalConfig config;
    bool active=false, initialized=false, filterReady=false, falling=false, configured=false, starting=false;
    uint32_t admitted=0, previous=0, fallingAt=0, startedAt=0;
    double command=0, filtered=0, target=0, duty=0;
public:
    static constexpr int DisconnectedOutput = 130;
    void configure(const ExternalConfig& value);
    void receive(bool allowed,int request,double demand,bool manual,uint32_t now);
    int tick(uint32_t now,double ambient);
    bool remote() const { return active; }
    int requested() const { return static_cast<int>(std::lround(target)); }
    const char* revision() const { return configured?config.revision:""; }
    static int electrical(int logical) { return 255-std::clamp(logical,0,255); }
};
class AmbientFilter {
    double value=NAN;
public:
    double update(double reading) {
        if (!std::isfinite(reading)||reading < -40||reading > 100) { value=NAN;return value; }
        value=std::isfinite(value)&&reading<45?value+(reading-value)*0.25:reading;return value;
    }
};
}
