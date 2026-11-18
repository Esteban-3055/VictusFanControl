#pragma once
#include "../include/WifiRecovery.h"
#include <stdexcept>
#include <iostream>
inline void wifiRecoveryTests() {
    auto check=[](bool b,const char* text){if(!b)throw std::runtime_error(text);};
    victus::WifiRecovery retry;
    check(!retry.retry(0,true,false,0),"WiFi boot needs an association grace period");
    check(!retry.retry(59999,true,false,0)&&retry.retry(60000,true,false,0),"WiFi stalled initial association retry deadline");
    check(!retry.retry(179999,true,false,0)&&retry.retry(180000,true,false,0),"WiFi recovery backoff 120 seconds");
    check(!retry.retry(419999,true,false,0)&&retry.retry(420000,true,false,0),"WiFi recovery backoff 240 seconds");
    check(!retry.retry(719999,true,false,0)&&retry.retry(720000,true,false,0),"WiFi recovery backoff capped at 300 seconds");
    check(!retry.retry(720001,true,true,720001)&&retry.attempts()==0,"WiFi recovered connection resets watchdog");
    check(!retry.retry(800000,true,false,800000),"Router loss gets its own grace period");
    check(!retry.retry(860000,true,false,850000),"Active AutoReconnect must not be interrupted");
    check(retry.retry(910000,true,false,850000),"Silent terminal disconnect after router restart never retried");
    check(!retry.retry(920000,false,false,850000)&&retry.attempts()==0,"Provisioning/disabled WiFi must not reconnect");
    const uint32_t at=0xfffffff0u;
    check(!retry.retry(at,true,false,at)&&!retry.retry(at+59999u,true,false,at)&&retry.retry(at+60000u,true,false,at),"WiFi watchdog must survive millis wrap");
    std::cout<<"PASS WiFi recovery: silent boot/router failure, association-event grace, 60/120/240/300 second backoff, connection/provisioning reset, millis wrap; no hardware IO\n";
}
