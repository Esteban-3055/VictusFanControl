#pragma once
#include "ExternalController.h"
#include <cstring>
#include <stdexcept>
#include <string>

inline void maximumOutputTests() {
    auto check=[](bool ok,const char* why){if(!ok)throw std::runtime_error(why);};
    using namespace victus;
    ExternalConfig c;
    std::strcpy(c.revision,"7234567890abcdef1234567890abcdef");
    // Reproduce the reported plateau with the saved curve and response values.
    c.count=7;c.points[0]={0,0};c.points[1]={35,0};c.points[2]={46,90};
    c.points[3]={56,130};c.points[4]={70,130};c.points[5]={88,255};c.points[6]={100,255};
    c.rise=2;c.fall=1;c.filterMs=1500;c.hysteresis=3;c.downHoldMs=12600;c.startMs=115;c.startOutput=255;
    ExternalController fan;fan.configure(c);
    for(uint32_t t=0;t<180000;t+=20) {
        if(t%1000==0)fan.receive(true,27,85.84,false,t); // Interpolates to 240 before asking for 255.
        fan.tick(t,24);
    }
    int previous=fan.tick(180000,24),oneSecondAgo=previous;
    check(previous>=230&&previous<=245,"fixture running near 240 before plateau");
    for(uint32_t t=180020;t<=360000;t+=20) {
        if(t==180020||t%1000==0)fan.receive(true,27,100,false,t);
        const int duty=fan.tick(t,24);
        check(duty>=previous&&duty-previous<=1,"maximum settling must still follow the configured rise");
        if(t%1000==0) {check(duty-oneSecondAgo<=6,"maximum settling respects 2 percent/s");oneSecondAgo=duty;}
        previous=duty;
    }
    if(c.evaluate(100)!=255||fan.requested()!=255||previous!=255)
        throw std::runtime_error("255 plateau remains inside hysteresis: target="+std::to_string(fan.requested())+", applied="+std::to_string(previous));

    // A request just below maximum must not be promoted to full output.
    auto near=c;near.count=2;near.points[0]={0,0};near.points[1]={100,254};
    std::strcpy(near.revision,"8234567890abcdef1234567890abcdef");
    near.startMs=near.startOutput=0;
    ExternalController partial;partial.configure(near);
    for(uint32_t t=0;t<=180000;t+=20) {
        if(t%1000==0)partial.receive(true,27,100,false,t);
        check(partial.tick(t,NAN)<255,"254 must not receive the full-output endpoint anchor");
    }
}
