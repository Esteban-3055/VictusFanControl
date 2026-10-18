#pragma once
#include "ExternalController.h"
#include <cstring>
#include <stdexcept>

inline void responseTests() {
    auto check=[](bool ok,const char* message){if(!ok)throw std::runtime_error(message);};
    victus::ExternalConfig c;
    std::strcpy(c.revision,"1234567890abcdef1234567890abcdef");
    c.count=2;c.points[0]={0,0};c.points[1]={100,255};
    c.filterMs=0;c.hysteresis=0;c.downHoldMs=0;c.startMs=0;c.startOutput=0;
    c.rise=17;c.fall=23;
    victus::ExternalController ramp;ramp.configure(c);
    ramp.receive(true,27,100,false,0);check(ramp.tick(0,24)==0,"configured zero startup output");
    check(ramp.tick(100,24)==4 && ramp.tick(200,24)==9,"rise 17 percent/s converts to logical duty/s");
    ramp.receive(true,27,0,false,200);
    check(ramp.tick(300,24)==3,"fall 23 percent/s is independent of rise");

    c.startOutput=201;c.startMs=1234;c.points[1].output=50;
    victus::ExternalController start;start.configure(c);
    start.receive(true,27,100,false,0);check(start.tick(0,24)==201,"custom startup output applied on boot");
    for(uint32_t t=20;t<=1220;t+=20) {
        if(t==1000)start.receive(true,27,100,false,t);
        check(start.tick(t,24)==201,"custom startup duration held in milliseconds");
    }
    check(start.tick(1233,24)==201 && start.tick(1254,24)<201,"startup expires at configured duration");
    auto renewed=c;std::strcpy(renewed.revision,"2234567890abcdef1234567890abcdef");
    start.configure(renewed);start.receive(true,27,100,false,1274);
    check(start.tick(1274,24)<201,"new configuration or reconnection does not restart a running fan");

    c.startMs=0;c.startOutput=100;c.points[1].output=255;c.filterMs=2000;
    victus::ExternalController ema;ema.configure(c);
    ema.receive(true,27,0,false,0);ema.tick(0,24);
    ema.receive(true,27,100,false,100);ema.tick(100,24);
    check(ema.requested()==12,"EMA uses dt/(filterMs/1000+dt), not raw milliseconds");

    c.filterMs=0;c.hysteresis=7;c.points[1].output=100;
    victus::ExternalController hysteresis;hysteresis.configure(c);
    hysteresis.receive(true,27,50,false,0);hysteresis.tick(0,24);
    hysteresis.receive(true,27,56,false,100);hysteresis.tick(100,24);
    check(hysteresis.requested()==50,"hysteresis measured in logical 0-255 levels");
    hysteresis.receive(true,27,57,false,200);hysteresis.tick(200,24);
    check(hysteresis.requested()==57,"hysteresis threshold inclusive");

    c.hysteresis=0;c.downHoldMs=1000;
    victus::ExternalController hold;hold.configure(c);
    hold.receive(true,27,100,false,0);hold.tick(0,24);
    hold.receive(true,27,40,false,100);hold.tick(100,24);
    hold.tick(1099,24);check(hold.requested()==100,"descent waits full configured hold");
    hold.tick(1100,24);check(hold.requested()==40,"descent confirmation expires in milliseconds");
}
