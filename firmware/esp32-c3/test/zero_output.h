#pragma once
#include "ExternalController.h"
#include "../include/BoardConfig.h"
#include <cstring>
#include <stdexcept>

inline void zeroOutputTests() {
    auto check=[](bool value,const char* why){if(!value)throw std::runtime_error(why);};
    victus::ExternalConfig config;
    std::strcpy(config.revision,"0123456789abcdef0123456789abcdef");
    config.count=3;config.points[0]={0,0};config.points[1]={20,0};config.points[2]={100,255};
    config.manualOutput=0;
    check(config.valid() && config.evaluate(0)==0 && config.evaluate(20)==0,"zero plateau config");
    victus::ExternalController fan;fan.configure(config);
    int last=255;
    for(uint32_t t=0;t<60000;t+=20) {
        if(t%1000==0)fan.receive(true,27,0,false,t);
        int duty=fan.tick(t,24);
        check(duty>=0 && duty<=last && last-duty<=1,"stop preserves monotone bounded fall");
        last=duty;
    }
    check(last==0 && fan.remote() && fan.requested()==0,"EMA/hysteresis settle to exact zero");
    check(board::LedcDutyFrom8Bit(victus::ExternalController::electrical(0))==16383,"stop is full inverted electrical duty");
    auto timedOut=fan;check(timedOut.tick(61000,24)>=89 && !timedOut.remote(),"lease expiry overrides stop");
    auto local=fan;local.receive(false,27,0,false,60000);
    check(local.tick(60000,24)>=89 && !local.remote(),"authority withdrawal overrides stop");
    auto hot=fan;check(hot.tick(60000,35.01)>=30,"warm ambient retains original floor above 35C");
    check(hot.tick(60020,40)>=143 && hot.tick(60040,45)==255,"ambient floors bypass stop");
    auto fault=fan;check(fault.tick(60000,NAN)==255 && !fault.remote(),"sensor failure overrides stop immediately");
    fan.receive(true,27,50,false,60000);
    check(fan.tick(60000,24)==255,"stopped fan gets configured restart pulse");
    fan.receive(true,27,50,false,60100);fan.tick(60100,24);
    check(fan.tick(60799,24)==255,"restart hold duration");
    check(fan.tick(60900,24)<255,"request renewal does not repeat startup pulse");

    // Zero start output or zero duration must not continually restart the
    // startup timer or leave a small positive demand inside the zero deadband.
    for(int startMs:{0,800}) {
        auto small=config;small.count=2;small.points[0]={0,0};small.points[1]={100,1};
        small.startOutput=0;small.startMs=startMs;
        victus::ExternalController low;low.configure(small);
        for(uint32_t t=0;t<60000;t+=20) {
            if(t%1000==0)low.receive(true,27,0,false,t);
            low.tick(t,24);
        }
        check(low.tick(60000,24)==0,"small-output fixture stopped");
        low.receive(true,27,100,false,60020);
        for(uint32_t t=60020;t<=61020;t+=20)low.tick(t,24);
        check(low.tick(61040,24)==1,"positive output below hysteresis restarts without pulse");
    }
    auto direct=config;direct.filterMs=0;direct.hysteresis=0;direct.downHoldMs=0;direct.startMs=0;
    victus::ExternalController manual;manual.configure(direct);
    for(uint32_t t=0;t<20000;t+=20) {
        if(t%1000==0)manual.receive(true,27,NAN,true,t);
        manual.tick(t,24);
    }
    check(manual.tick(20000,24)==0 && manual.remote(),"Manual can request zero");
    direct.input=1;std::strcpy(direct.revision,"1123456789abcdef0123456789abcdef");
    manual.configure(direct);
    manual.receive(true,0,NAN,true,20020);
    check(manual.tick(20020,24)==0 && manual.remote(),"accepted request can select zero curve plateau");
}
