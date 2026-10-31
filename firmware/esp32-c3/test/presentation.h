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
                         offset<40000?LcdPage::Demand:LcdPage::Connection;
        check(lcdPage(t)==expected,"LCD exact 30/5/5/5 boundaries, two cycles");
    }
    uint32_t start=UINT32_MAX-20000, origin=start;
    for(uint32_t t=0;t<180000;t+=250)
        check(lcdElapsed(origin+t,start)==t%45000,"page clock survives millis rollover");
    LcdReadings data;
    data.telemetryFresh=true;data.pcAuthenticated=true;data.wifi=true;data.pwmEnabled=true;
    data.cpuPct=100;data.gpuPct=0;data.ramPct=65;data.demandPct=42;
    data.hyperxPct=55;data.cpuTempC=130;data.gpuTempC=-20;data.ambientC=21.2;data.pwmDuty=96;
    char top[17]{},bottom[17]{};
    renderLcd(LcdPage::Temperatures,data,top,bottom);
    check(std::string(top)=="CPU:130CGPU:-20C" && std::strstr(bottom,"Amb:21C Hyp:55%"),"component and ambient temperatures on first page");
    renderLcd(LcdPage::Usage,data,top,bottom);
    check(std::string(top)=="CPU:100%GPU:  0%" && std::strstr(bottom,"RAM: 65%"),"CPU GPU RAM in one page");
    renderLcd(LcdPage::Demand,data,top,bottom);
    check(std::strstr(top,"DEMANDA: 42%") && std::strstr(bottom,"38% (96)"),"demand and applied logical PWM percent and duty");
    data.pwmDuty=0;renderLcd(LcdPage::Demand,data,top,bottom);
    check(std::strstr(bottom,"0% (0)"),"real applied zero is not OFF or null");data.pwmDuty=96;
    renderLcd(LcdPage::Connection,data,top,bottom);
    check(std::strstr(top,"PC: conectado") && std::strstr(bottom,"WiFi: conectado"),"PC and WiFi independent");
    for(double value:{-20.0,0.0,99.0,100.0,130.0,131.0,static_cast<double>(NAN),static_cast<double>(INFINITY)}) {
        data.cpuTempC=data.gpuTempC=data.cpuPct=data.gpuPct=data.ramPct=data.demandPct=value;
        data.ambientC=value;data.hyperxPct=value;
        for(auto page:{LcdPage::Temperatures,LcdPage::Usage,LcdPage::Demand,LcdPage::Connection}) {
            renderLcd(page,data,top,bottom);
            check(std::strlen(top)==16 && std::strlen(bottom)==16,"every row exactly 16 plus NUL");
        }
    }
    data.telemetryFresh=false;
    data.ambientC=21.2;
    renderLcd(LcdPage::Temperatures,data,top,bottom);
    check(std::strstr(top,"CPU:--C") && std::strstr(top,"GPU:--C") && std::strstr(bottom,"Amb:21C"),"stale PC does not hide fresh independent ambient");
    renderLcd(LcdPage::Demand,data,top,bottom);
    check(std::strstr(top,"DEMANDA: --%") && std::strstr(bottom,"38% (96)"),"stale demand hidden while applied PWM remains visible");
    renderLcd(LcdPage::Usage,data,top,bottom);
    check(std::strstr(top,"--%") && std::strstr(bottom,"--%"),"no stale usage");
    data.ambientC=NAN;
    renderLcd(LcdPage::Temperatures,data,top,bottom);
    check(std::strstr(bottom,"Amb:--C"),"ambient null on first page");
    renderLcd(LcdPage::Demand,data,top,bottom);
    check(std::strstr(top,"DEMANDA: --%") && std::strstr(bottom,"38% (96)"),"missing ambient does not hide independent applied duty");
    renderLcd(LcdPage::Connection,data,top,bottom);
    check(std::strstr(top,"sin datos") && std::strstr(bottom,"conectado"),"stale PC with connected WiFi");
    data.pcAuthenticated=false;data.wifi=false;
    renderLcd(LcdPage::Connection,data,top,bottom);
    check(std::strstr(top,"desconectado") && std::strstr(bottom,"sin red"),"disconnected labels fit");
    data.telemetryFresh=true;data.cpuTempC=55;data.gpuTempC=42;data.demandPct=35;
    renderLcd(LcdPage::Temperatures,data,top,bottom);
    check(std::strstr(top,"55C") && std::strstr(top,"42C") && std::strstr(bottom,"Amb:--C"),"NTC failure does not replace notebook telemetry");
    renderLcd(LcdPage::Demand,data,top,bottom);
    check(std::strstr(top,"DEMANDA: 35%") && std::strstr(bottom,"38% (96)"),"NTC failure does not replace fresh demand or applied duty");
    data.ambientC=21.2;data.pwmEnabled=false;
    renderLcd(LcdPage::Demand,data,top,bottom);
    check(std::strstr(top,"DEMANDA: 35%") && std::strstr(bottom,"PWM:-- (OFF)"),"monitor image does not claim applied PWM");

    Measurement m;
    check(!m.linkFresh(0) && std::isnan(m.fresh(55,0,0)),"handshake alone is not telemetry");
    m.received=true;m.receivedAt=UINT32_MAX-1000;
    check(m.fresh(55,0,998)==55 && std::isnan(m.fresh(55,0,999)),"transport freshness expires at 2000ms across rollover");
    m.receivedAt=1000;
    check(m.fresh(55,4999,1000)==55 && std::isnan(m.fresh(55,4999,1001)),"source age plus local residence exact expiry");
    check(std::isnan(m.fresh(55,5000,1000)),"missing or old timestamp never fresh");
    check(m.fresh(0,0,1000)==0 && std::isnan(m.fresh(NAN,0,1000)),"zero preserved, null not invented");

    data.telemetryFresh=true;data.ambientC=-40;data.hyperxPct=100;
    renderLcd(LcdPage::Temperatures,data,top,bottom);
    check(std::string(bottom)=="Amb:-40CHyp:100%","worst-case ambient/HyperX row fits without truncating percent");
    data.hyperxPct=NAN;renderLcd(LcdPage::Temperatures,data,top,bottom);
    check(std::strstr(bottom,"Hyp:--%"),"unavailable headset never becomes zero");
    m.received=true;m.receivedAt=UINT32_MAX-1000;m.hyperxPct=55;m.hyperxAge=178999;
    check(m.freshHyperx(UINT32_MAX)==55&&std::isnan(m.freshHyperx(0)),"headset source age plus residence across rollover");
    m.receivedAt=1000;m.hyperxAge=60000;
    check(m.freshHyperx(2000)==55&&std::isnan(m.freshHyperx(3000)),"PC link loss hides cached headset independently of minute polling");
    HyperXDisplayState alert;
    alert.observe(20,1000,0,UINT32_MAX-2000);
    check(alert.overlay(UINT32_MAX-2000,top,bottom)&&std::strstr(top,"BAJA"),"low battery overrides any normal page immediately");
    check(alert.overlay(UINT32_MAX-1000,top,bottom)&&std::string(top)==std::string(16,' '),"low alert blank phase without blocking");
    for(unsigned t=0;t<6000;t+=250) {
        alert.observe(20,1000,0,UINT32_MAX-2000+t); // repeated frames are not new acquisitions
        check(alert.overlay(UINT32_MAX-2000+t,top,bottom),"duplicate frames do not change alert lifetime");
        check((top[0]!=' ')==((t/1000)%2==0),"exact three visible/blank cycles across millis rollover");
    }
    check(!alert.overlay(UINT32_MAX-2000+6000,top,bottom),"overlay returns to normal page at exact deadline");
    check(alert.lastPercent()==20,"TCP loss retains the last battery percentage in RAM");
    data.telemetryFresh=false;data.hyperxCached=true;data.hyperxPct=alert.lastPercent();data.ambientC=21;
    renderLcd(LcdPage::Temperatures,data,top,bottom);
    check(std::strstr(top,"CPU:--C")&&std::strstr(bottom,"Hyp:20*"),"cached battery marked previous while CPU/GPU expire");
    alert.observe(10,2000,0,7000);
    check(alert.overlay(7000,top,bottom)&&std::strstr(top,"CRITICA"),"critical transition has priority");
    alert.observe(10,3000,0,8000);
    check(!alert.overlay(17000,top,bottom),"new readings within critical range do not repeat the warning");
    alert.observe(30,4000,0,18000);alert.observe(20,5000,0,19000);
    check(alert.overlay(19000,top,bottom)&&std::strstr(top,"BAJA"),"five-point recovery rearms low warning");
    alert.observe(10,6000,0,19500);
    check(alert.overlay(19500,top,bottom)&&std::strstr(top,"CRITICA"),"critical preempts a running low warning");
    HyperXDisplayConfig disabled;disabled.criticalEnabled=false;alert.configure(disabled);
    check(!alert.overlay(19500,top,bottom),"disabling critical cancels an active warning");
    HyperXDisplayState timed;HyperXDisplayConfig cfg;cfg.lowDurationMs=4000;cfg.lowBlinkCycles=2;timed.configure(cfg);timed.observe(20,1000,0,0);
    check(timed.overlay(3999,top,bottom)&&!timed.overlay(4000,top,bottom),"custom duration and blink count applied");
    HyperXDisplayState old;old.observe(10,1000,180000,0);
    check(std::isnan(old.lastPercent())&&!old.overlay(0,top,bottom),"old battery cannot populate cache or trigger an alert");
    HyperXDisplayState offline;
    check(!offline.overlay(0,top,bottom,false),"offline boot without a reading cannot invent an alarm");
    offline.observe(20,1000,0,0);check(!offline.overlay(6000,top,bottom),"connected finite alert completes normally");
    uint32_t offlineOrigin=UINT32_MAX-2500;
    for(uint32_t t=0;t<120000;t+=250) {
        offline.observe(20,1000,0,offlineOrigin+t); // Cached frame cannot reset the blink phase.
        check(offline.overlay(offlineOrigin+t,top,bottom,false),"disconnected cached low alert repeats indefinitely");
        check((top[0]!=' ')==((t/1000)%2==0),"offline repeated blink phase remains exact across rollover");
        check(std::strlen(top)==16&&std::strlen(bottom)==16,"offline alert stays 16 by 2");
        if(top[0]!=' ')check(std::strstr(bottom,"20% *"),"offline alarm identifies previous battery data");
    }
    check(!offline.overlay(offlineOrigin+120000,top,bottom,true),"reconnection ends repeated cached warning without replaying it");
    offline.observe(10,2000,0,1000);
    check(offline.overlay(1000,top,bottom,true)&&std::strstr(top,"CRITICA"),"new authenticated critical transition still produces a finite warning");
    check(offline.overlay(2000,top,bottom,false)&&std::strstr(top,"CRITICA"),"critical cached warning wins offline");
    check(offline.overlay(122000,top,bottom,false),"offline critical alert does not expire");
    offline.observe(55,3000,0,123000);
    check(!offline.overlay(123000,top,bottom,true)&&!offline.overlay(124000,top,bottom,false),"fresh recovery cancels stale cached warning on reconnect");
    HyperXDisplayConfig endpoint;
    for(int critical:{0,100}) {
        endpoint.criticalPercent=critical;check(endpoint.valid(),"critical 0 and 100 are supported independently of low");
        HyperXDisplayState edge;edge.configure(endpoint);edge.observe(critical==0?0:55,1000,0,0);
        check(edge.overlay(0,top,bottom)&&std::strstr(top,"CRITICA"),"critical endpoint works with genuine zero and overlaps");
        check(edge.overlay(1000,top,bottom,false)&&std::strstr(bottom,"*"),"endpoint warning repeats as cached without PC");
        endpoint.criticalEnabled=false;endpoint.lowEnabled=false;edge.configure(endpoint);
        check(!edge.overlay(2000,top,bottom,false),"disabled alerts stop offline repetition");endpoint.criticalEnabled=endpoint.lowEnabled=true;
    }
    endpoint.criticalPercent=-1;check(!endpoint.valid(),"negative critical percentage rejected");
    endpoint.criticalPercent=101;check(!endpoint.valid(),"critical percentage above 100 rejected");
    cfg.lowDurationMs=1000;cfg.lowBlinkCycles=20;check(!cfg.valid(),"blink rate cannot exceed LCD refresh capability");
    data.hyperxCached=false;
    AmbientAverage40 average;
    for(unsigned i=0;i<39;++i)check(std::isnan(average.update(20,i*100)),"no partial mean before forty samples");
    check(average.update(60,3900)==21,"mean uses all forty converted temperatures");
    check(average.update(20,4000)==21,"rolling window replaces exactly one oldest sample");
    check(std::isnan(average.update(NAN,4100))&&average.samples()==0,"NTC fault clears mean immediately");
    for(unsigned i=0;i<40;++i)average.update(25,4200+i*100);
    check(average.update(25,8200)==25,"full recovery refills forty samples");
    check(std::isnan(average.update(25,11200))&&average.samples()==1,"sampling gap invalidates the old window");
    AmbientAverage40 wrapAverage;
    for(unsigned i=0;i<40;++i)wrapAverage.update(23,UINT32_MAX-2000+i*100);
    check(wrapAverage.update(23,1999)==23,"ambient averaging survives millis rollover");

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
