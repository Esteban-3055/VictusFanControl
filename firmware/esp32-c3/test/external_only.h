#pragma once
#include "ExternalController.h"
#include <cstring>
#include <stdexcept>

inline void externalOnlyTests() {
    auto check=[](bool ok,const char* why){if(!ok)throw std::runtime_error(why);};
    using namespace victus;
    ExternalConfig c;
    std::strcpy(c.revision,"6234567890abcdef1234567890abcdef");
    c.count=2;c.points[0]={0,0};c.points[1]={100,255};
    c.rise=c.fall=1;c.filterMs=10000;c.hysteresis=3;c.downHoldMs=30000;
    // Even a stored startup pulse must not execute without a remote request.
    c.startMs=800;c.startOutput=255;
    ExternalController boot;boot.configure(c);
    check(boot.tick(0,NAN)==0,"boot without NTC or PC starts at logical zero");
    for(uint32_t t=20;t<=2000;t+=20)
        check(boot.tick(t,100)<=6&&!boot.remote(),"boot ramps to Local 130, never pulses to 255");

    for(double demand:{0.0,27.0,100.0}) {
        ExternalController seed;seed.configure(c);seed.receive(true,27,demand,false,0);seed.tick(0,24);
        for(double ambient:{-100.0,-40.0,24.0,35.01,40.0,45.0,100.0,101.0,static_cast<double>(NAN),static_cast<double>(INFINITY)}) {
            auto reference=seed,probe=seed;
            for(uint32_t t=20;t<=5000;t+=20) {
                if(t==1000) { reference.receive(true,27,demand,false,t);probe.receive(true,27,demand,false,t); }
                check(reference.tick(t,24)==probe.tick(t,ambient),"ambient never alters the actuator, including lease expiry");
                check(reference.remote()==probe.remote(),"NTC cannot revoke remote authority");
            }
        }
    }

    ExternalController low;low.configure(c);low.receive(true,27,0,false,0);
    check(low.tick(0,NAN)==0,"zero remote command does not start a pulse");
    low.receive(true,27,0,false,1000);low.tick(1000,NAN);
    int previous=low.tick(2999,NAN);
    check(low.remote()&&previous==0,"lease remains active through 1999ms");
    check(low.tick(3000,NAN)==0&&!low.remote(),"lease expires at 2000ms without an output jump");
    int oneSecondAgo=0;
    for(uint32_t t=3020;t<=243000;t+=20) {
        const int duty=low.tick(t,NAN);
        check(duty>=previous&&duty-previous<=1&&duty<=130,"low output ramps monotonically to 130");
        if(t%1000==0) {check(duty-oneSecondAgo<=3,"Local rise respects configured speed");oneSecondAgo=duty;}
        previous=duty;
    }
    check(previous==130&&low.requested()==130,"Local settles to exactly 130, outside hysteresis residual");

    ExternalController high;high.configure(c);high.receive(true,27,100,false,0);
    check(high.tick(0,24)==255,"explicit remote startup pulse remains configurable");
    high.receive(false,27,0,false,20);
    previous=high.tick(20,45);check(previous==255&&!high.remote(),"withdrawal never jumps down from high output");
    oneSecondAgo=previous;
    for(uint32_t t=40;t<=240000;t+=20) {
        const int duty=high.tick(t,45);
        check(duty<=previous&&previous-duty<=1&&duty>=130,"high output ramps monotonically to 130");
        if(t%1000==0) {check(oneSecondAgo-duty<=3,"Local descent respects configured speed");oneSecondAgo=duty;}
        previous=duty;
    }
    check(previous==130&&high.requested()==130,"Local settles to exactly 130 from above");
    high.receive(true,27,50,false,240020);
    check(high.tick(240020,NAN)<=130&&high.remote(),"reconnection from Local does not pulse a running fan");
}
