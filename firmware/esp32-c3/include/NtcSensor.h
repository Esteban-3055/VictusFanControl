#pragma once
#include "BoardConfig.h"
#include <cmath>
#include <array>

namespace victus {
// Forty independent converted samples at 100 ms: rolling four-second mean.
// A fault or acquisition gap clears the entire window; old readings never mask it.
class AmbientAverage40 {
    std::array<double,40> readings{};
    unsigned count=0,index=0;
    double sum=0;
    uint32_t last=0;
public:
    double update(double c,uint32_t now) {
        if(count && static_cast<uint32_t>(now-last)>=3000) {count=index=0;sum=0;}
        last=now;
        if(!std::isfinite(c)||c< -40||c>100) {count=index=0;sum=0;return NAN;}
        if(count==readings.size()) sum-=readings[index]; else ++count;
        readings[index]=c;sum+=c;index=(index+1)%readings.size();
        return count==readings.size()?sum/readings.size():NAN;
    }
    unsigned samples() const {return count;}
};
struct AmbientSample {
    double value = NAN;
    uint32_t at = 0;
    double current(uint32_t now) const { return static_cast<uint32_t>(now-at)<3000 ? value : NAN; }
};
struct NtcResult { double c = NAN; const char* reason = "OK"; };
inline NtcResult convertNtc(unsigned raw, unsigned mv) {
    if (raw<8) return {NAN,"RAW_LOW"};
    if (raw>4087) return {NAN,"RAW_HIGH"};
    if (mv<20) return {NAN,"MV_LOW"};
    if (mv>2450) return {NAN,"MV_HIGH"};
    double resistance=board::FixedResistorOhms*mv/(board::SupplyMilliVolts-mv);
    if (!std::isfinite(resistance)||resistance<=0) return {NAN,"BAD_RESISTANCE"};
    double c=1.0/(1.0/298.15+std::log(resistance/board::NtcNominalOhms)/board::NtcBeta)-273.15;
    if (!std::isfinite(c)||c< -40||c>100) return {NAN,"TEMP_RANGE"};
    return {c,"OK"};
}
} // namespace victus
