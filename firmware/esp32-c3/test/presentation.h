#pragma once
#include "../include/LcdPresentation.h"
#include "../include/LcdTransport.h"
#include "../include/NtcSensor.h"
#include "VictusProtocol.h"
#include <stdexcept>
#include <vector>
#include <string>

inline void presentationTests() {
    auto check=[](bool ok,const char* message) { if(!ok) throw std::runtime_error(message); };
    using namespace victus;
    check(LcdCycleMs==45000,"LCD total 45s");
    for(uint32_t t=0;t<90000;++t) {
        uint32_t offset=t%45000;
        LcdPage expected=offset<30000?LcdPage::Temperatures:offset<35000?LcdPage::Usage:
                         offset<40000?LcdPage::Ambient:LcdPage::Connection;
        check(lcdPage(t)==expected,"LCD exact 30/5/5/5 boundaries, two cycles");
    }
    uint32_t start=UINT32_MAX-20000, origin=start;
    for(uint32_t t=0;t<180000;t+=250)
        check(lcdElapsed(origin+t,start)==t%45000,"page clock survives millis rollover");
    LcdReadings data;
    data.telemetryFresh=true;data.pcAuthenticated=true;data.wifi=true;data.pwmEnabled=true;
    data.cpuPct=100;data.gpuPct=0;data.ramPct=65;data.demandPct=42;
    data.cpuTempC=130;data.gpuTempC=-20;data.ambientC=21.2;data.pwmDuty=96;
    char top[17]{},bottom[17]{};
    renderLcd(LcdPage::Temperatures,data,top,bottom);
    check(std::string(top)=="CPU:130CGPU:-20C" && std::strstr(bottom,"DEMANDA: 42%"),"exclusive thermal+demand layout");
    renderLcd(LcdPage::Usage,data,top,bottom);
    check(std::string(top)=="CPU:100%GPU:  0%" && std::strstr(bottom,"RAM: 65%"),"CPU GPU RAM in one page");
    renderLcd(LcdPage::Ambient,data,top,bottom);
    check(std::strstr(top,"21.2C") && std::strstr(bottom,"38% (96)"),"applied logical PWM percent and duty");
    data.pwmDuty=0;renderLcd(LcdPage::Ambient,data,top,bottom);
    check(std::strstr(bottom,"0% (0)"),"real applied zero is not OFF or null");data.pwmDuty=96;
    renderLcd(LcdPage::Connection,data,top,bottom);
    check(std::strstr(top,"PC: conectado") && std::strstr(bottom,"WiFi: conectado"),"PC and WiFi independent");
    for(double value:{-20.0,0.0,99.0,100.0,130.0,131.0,static_cast<double>(NAN),static_cast<double>(INFINITY)}) {
        data.cpuTempC=data.gpuTempC=data.cpuPct=data.gpuPct=data.ramPct=data.demandPct=value;
        for(auto page:{LcdPage::Temperatures,LcdPage::Usage,LcdPage::Ambient,LcdPage::Connection}) {
            renderLcd(page,data,top,bottom);
            check(std::strlen(top)==16 && std::strlen(bottom)==16,"every row exactly 16 plus NUL");
        }
    }
    data.telemetryFresh=false;
    renderLcd(LcdPage::Temperatures,data,top,bottom);
    check(std::strstr(top,"CPU:--C") && std::strstr(top,"GPU:--C") && std::strstr(bottom,"DEMANDA: --%"),"no stale temperatures or demand");
    renderLcd(LcdPage::Usage,data,top,bottom);
    check(std::strstr(top,"--%") && std::strstr(bottom,"--%"),"no stale usage");
    data.ambientC=NAN;
    renderLcd(LcdPage::Ambient,data,top,bottom);
    check(std::strstr(top,"--C") && std::strstr(bottom,"NTC FAULT"),"fault hides prior applied duty");
    renderLcd(LcdPage::Connection,data,top,bottom);
    check(std::strstr(top,"sin datos") && std::strstr(bottom,"conectado"),"stale PC with connected WiFi");
    data.pcAuthenticated=false;data.wifi=false;
    renderLcd(LcdPage::Connection,data,top,bottom);
    check(std::strstr(top,"desconectado") && std::strstr(bottom,"sin red"),"disconnected labels fit");
    data.telemetryFresh=true;data.cpuTempC=55;data.gpuTempC=42;data.demandPct=35;
    renderLcd(LcdPage::Temperatures,data,top,bottom);
    check(std::strstr(top,"55C") && !std::strstr(bottom,"NTC"),"NTC failure does not replace notebook telemetry");
    data.ambientC=21.2;data.pwmEnabled=false;
    renderLcd(LcdPage::Ambient,data,top,bottom);
    check(std::strstr(top,"21.2C") && std::strstr(bottom,"PWM:-- (OFF)"),"monitor image does not claim applied PWM");

    Measurement m;
    check(!m.linkFresh(0) && std::isnan(m.fresh(55,0,0)),"handshake alone is not telemetry");
    m.received=true;m.receivedAt=UINT32_MAX-1000;
    check(m.fresh(55,0,998)==55 && std::isnan(m.fresh(55,0,999)),"transport freshness expires at 2000ms across rollover");
    m.receivedAt=1000;
    check(m.fresh(55,4999,1000)==55 && std::isnan(m.fresh(55,4999,1001)),"source age plus local residence exact expiry");
    check(std::isnan(m.fresh(55,5000,1000)),"missing or old timestamp never fresh");
    check(m.fresh(0,0,1000)==0 && std::isnan(m.fresh(NAN,0,1000)),"zero preserved, null not invented");

    check(std::abs(convertNtc(2439,1792).c-21.2)<0.1,"reported NTC sample valid");
    check(std::abs(convertNtc(2048,1650).c-25)<0.01,"NTC nominal divider");
    for(auto reading:{convertNtc(0,1792),convertNtc(4095,1792),convertNtc(2439,19),convertNtc(2439,2451)})
        check(std::isnan(reading.c),"NTC limits unchanged");
    AmbientSample sample{21.2,UINT32_MAX-1000};
    check(sample.current(1998)==21.2 && std::isnan(sample.current(1999)),"NTC freshness exactly 3s across rollover");
    AmbientFilter filter;
    check(std::isfinite(filter.update(convertNtc(2439,1792).c)),"NTC recovery starts valid");
    check(std::isnan(filter.update(convertNtc(4095,1792).c)),"intermittent invalid NTC immediately invalidates filter");
    check(filter.update(21.2)==21.2 && filter.update(45)==45,"recovery has no stale EMA and hot NTC bypasses it");

    struct FakeBus {
        int writes=0,failAt=-1;
        std::vector<std::vector<uint8_t>> bytes;
        uint8_t write(const uint8_t* p,size_t n) {
            bytes.emplace_back(p,p+n);
            return ++writes==failAt?4:0;
        }
        void wait(uint32_t) {}
    } bus;
    LcdTransport<FakeBus> lcd(bus);
    check(lcd.initialize(),"LCD starts after full init");
    const int initializationWrites=bus.writes;
    check(bus.bytes.front()==std::vector<uint8_t>{8},"LCD recovery first drives E low");
    check(lcd.row(0x80,top) && lcd.row(0xc0,bottom),"full two row rewrite");
    const int totalWrites=bus.writes;
    // Fail each initialization/row transaction, including either nibble of
    // each character. No subsequent byte may be sent before reinitialization.
    for(int failure=1;failure<=totalWrites;++failure) {
        FakeBus faulty;faulty.failAt=failure;
        LcdTransport<FakeBus> test(faulty);
        test.initialize();test.row(0x80,top);test.row(0xc0,bottom);
        check(!test.ready() && faulty.writes==failure,"I2C failure aborts immediately");
        int stopped=faulty.writes;test.row(0x80,top);
        check(stopped==faulty.writes,"offline LCD never writes characters");
        faulty.failAt=-1;
        check(test.initialize() && faulty.writes==stopped+initializationWrites,"LCD retry performs complete resynchronization");
        check(faulty.bytes[stopped]==std::vector<uint8_t>{8},"partial pulse recovery forces E low");
        check(test.row(0x80,top) && test.row(0xc0,bottom),"LCD immediately accepts current rows after recovery");
    }
}
