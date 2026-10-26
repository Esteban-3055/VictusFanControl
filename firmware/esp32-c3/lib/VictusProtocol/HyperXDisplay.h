#pragma once
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <initializer_list>

namespace victus {
struct HyperXDisplayConfig {
    bool lowEnabled=true,criticalEnabled=true;
    int lowPercent=20,criticalPercent=10;
    int lowDurationMs=6000,criticalDurationMs=10000,lowBlinkCycles=3,criticalBlinkCycles=5;
    bool valid() const {
        auto timing=[](int ms,int cycles){return ms>=1000&&ms<=120000&&cycles>=1&&cycles<=20&&ms>=cycles*1000;};
        return criticalPercent>=10&&criticalPercent<=98&&lowPercent>criticalPercent&&lowPercent<=99&&
            timing(lowDurationMs,lowBlinkCycles)&&timing(criticalDurationMs,criticalBlinkCycles);
    }
};
// RAM only. Survives TCP/Wi-Fi reconnects, not power loss. Never written to NVS.
// Only newly acquired, fresh, authenticated samples may create an alert.
class HyperXDisplayState {
    HyperXDisplayConfig config;
    double percent=NAN;
    int64_t sampled=0;
    bool lowLatched=false,criticalLatched=false;
    uint32_t started=0;
    int active=0;
public:
    void configure(const HyperXDisplayConfig& c) {
        if(!c.valid())return;
        config=c;
        if((active==1&&!c.lowEnabled)||(active==2&&!c.criticalEnabled))active=0;
    }
    void observe(double value,int64_t at,uint32_t sourceAge,uint32_t now) {
        if(!std::isfinite(value)||value<0||value>100||sourceAge>=180000||at<=sampled)return;
        sampled=at;percent=value;
        if(value>=config.lowPercent+5)lowLatched=false;
        if(value>=config.criticalPercent+5)criticalLatched=false;
        if(value<=config.criticalPercent) {
            if(!criticalLatched&&config.criticalEnabled){active=2;started=now;}
            criticalLatched=true;lowLatched=true;
        } else if(value<=config.lowPercent) {
            if(!lowLatched&&config.lowEnabled){active=1;started=now;}
            lowLatched=true;
        }
        // A fresh recovery cancels an obsolete warning immediately.
        if((active==1&&value>config.lowPercent)||(active==2&&value>config.criticalPercent))active=0;
    }
    double lastPercent() const {return percent;}
    bool overlay(uint32_t now,char (&first)[17],char (&second)[17]) {
        if(!active)return false;
        uint32_t elapsed=now-started;
        uint32_t duration=active==2?config.criticalDurationMs:config.lowDurationMs;
        uint32_t cycles=active==2?config.criticalBlinkCycles:config.lowBlinkCycles;
        if(elapsed>=duration){active=0;return false;}
        std::memset(first,' ',16);std::memset(second,' ',16);first[16]=second[16]='\0';
        if(((elapsed*cycles*2)/duration)%2==0) {
            std::snprintf(first,17,"%s",active==2?"HYP BAT. CRITICA":"HYP BATERIA BAJA");
            std::snprintf(second,17,"Carga: %.0f%% aprox",percent);
            for(auto row:{first,second}) {auto n=std::strlen(row);while(n<16)row[n++]=' ';row[16]='\0';}
        }
        return true;
    }
};
}
