#pragma once
#include "ExternalController.h"
#include <cstring>
#include <stdexcept>

inline void smoothingTests() {
    auto check=[](bool ok,const char* why){if(!ok)throw std::runtime_error(why);};
    victus::ExternalConfig c;
    std::strcpy(c.revision,"4234567890abcdef1234567890abcdef");
    c.count=2;c.points[0]={0,0};c.points[1]={100,255};
    c.rise=c.fall=1;c.filterMs=10000;c.hysteresis=3;c.downHoldMs=30000;c.startMs=c.startOutput=0;
    victus::ExternalController fan;fan.configure(c);
    for(uint32_t t=0;t<240000;t+=20) {
        if(t%1000==0)fan.receive(true,27,0,false,t);
        fan.tick(t,24);
    }
    check(fan.tick(240000,24)==0 && fan.remote(),"smooth fixture settles at true zero");
    auto stopped=fan;
    int previous=0,oneSecondAgo=0;
    for(uint32_t t=240020;t<=360000;t+=20) {
        if(t==240020||t%1000==0)fan.receive(true,27,100,false,t);
        const int duty=fan.tick(t,24);
        check(duty>=previous&&duty-previous<=1,"smooth rise never jumps, including restart from zero");
        if(t%1000==0) {
            check(duty-oneSecondAgo<=3,"smooth rise at most 2.55 logical units/s plus integer rounding");
            oneSecondAgo=duty;
        }
        if(t==241000)check(duty<=3,"no 255 startup pulse after a full demand step");
        if(t==250000)check(duty<=26,"10s rise remains near 10 percent");
        if(t==339000)check(duty<255,"full scale cannot be crossed before 100 seconds");
        previous=duty;
    }
    check(previous==255,"sustained demand still reaches maximum");
    auto running=fan;
    oneSecondAgo=previous;
    for(uint32_t t=360020;t<=600000;t+=20) {
        if(t==360020||t%1000==0)fan.receive(true,27,0,false,t);
        const int duty=fan.tick(t,24);
        check(duty<=previous&&previous-duty<=1,"smooth descent has no abrupt drop");
        if(t<=390000)check(duty==255,"descent retains configured confirmation hold");
        if(t%1000==0) {
            check(oneSecondAgo-duty<=3,"smooth descent respects 1 percent/s");oneSecondAgo=duty;
        }
        previous=duty;
    }
    check(previous==0&&fan.remote(),"slow EMA/hysteresis still settle to exact stop");

    // Reproduce the user's 47 -> 48 percent edge with the smooth settings.
    c.count=5;c.points[0]={0,0};c.points[1]={47,0};c.points[2]={71,69};c.points[3]={87,145};c.points[4]={100,255};
    std::strcpy(c.revision,"5234567890abcdef1234567890abcdef");stopped.configure(c);
    int maximum=0;
    for(uint32_t t=240020;t<=250000;t+=20) {
        if(t==240020||t%1000==0)stopped.receive(true,27,48,false,t);
        maximum=std::max(maximum,stopped.tick(t,24));
    }
    check(maximum<=3,"user curve restart has no max-output kick");

    // All existing safety paths bypass these deliberately slow settings.
    auto fault=stopped;check(fault.tick(250020,NAN)==255&&!fault.remote(),"fault still immediate maximum");
    auto hot=stopped;check(hot.tick(250020,45)==255,"hot ambient still immediate maximum");
    auto warm=stopped;check(warm.tick(250020,40)>=143,"ambient floor still immediate");
    auto lease=stopped;check(lease.tick(252000,24)>=89&&!lease.remote(),"lease expiry retains Local floor");
    auto local=stopped;local.receive(false,27,48,false,250020);
    check(local.tick(250020,24)>=89&&!local.remote(),"authority withdrawal retains Local floor");
    auto hotRecovery=running;hotRecovery.tick(360020,45);
    check(hotRecovery.tick(360040,24)>=254,"cooling after hot override cannot jump down");
}
