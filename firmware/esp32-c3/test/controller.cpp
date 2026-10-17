#include "ExternalController.h"
#include "../include/BoardConfig.h"
#include "../include/LcdPresentation.h"
#include <cstring>
#include <iostream>
#include <stdexcept>
namespace {
void require(bool b,const char* s){if(!b)throw std::runtime_error(s);}
victus::ExternalConfig config(){victus::ExternalConfig c;std::strcpy(c.revision,"0123456789abcdef0123456789abcdef");return c;}
}
int controllerTests() {
    // 16x2 display must cycle even when telemetry is absent. Temperatures
    // occupy 30 seconds exactly and no stale Windows data is presented.
    using victus::LcdPage;
    require(victus::LcdCycleMs==45000,"LCD cycle 5+5+5+30 seconds");
    require(victus::lcdPage(0)==LcdPage::CpuGpu && victus::lcdPage(4999)==LcdPage::CpuGpu,"LCD CPU/GPU first five seconds");
    require(victus::lcdPage(5000)==LcdPage::RamDemand && victus::lcdPage(9999)==LcdPage::RamDemand,"LCD RAM/DEMANDA page");
    require(victus::lcdPage(10000)==LcdPage::FanStatus && victus::lcdPage(14999)==LcdPage::FanStatus,"LCD PWM status page");
    require(victus::lcdPage(15000)==LcdPage::Temperatures && victus::lcdPage(44999)==LcdPage::Temperatures,"LCD temperature exactly 30 seconds");
    require(victus::lcdPage(45000)==LcdPage::CpuGpu,"LCD cycle returns to first page");
    require(victus::lcdTemperatureRemaining(15000)==30000 && victus::lcdTemperatureRemaining(44999)==1,
            "temperature time-to-next-page");
    victus::LcdReadings screen{};
    screen.telemetryFresh=true;screen.cpuPct=100;screen.gpuPct=0;screen.ramPct=65;screen.demandPct=42;
    screen.cpuTempC=130;screen.gpuTempC=120;screen.ambientC=21.2;
    screen.pwmEnabled=true;screen.remote=true;screen.wifi=true;screen.pwmDuty=96;
    char top[17]{},bottom[17]{};
    victus::renderLcd(LcdPage::CpuGpu,screen,0,top,bottom);
    require(std::strlen(top)==16 && std::strstr(top,"100%") && std::strstr(bottom,"GPU:"),"LCD load formatting");
    victus::renderLcd(LcdPage::RamDemand,screen,0,top,bottom);
    require(std::strstr(bottom,"DEMANDA: 42%"),"LCD full demand label");
    victus::renderLcd(LcdPage::FanStatus,screen,0,top,bottom);
    require(std::strstr(top,"21.2") && std::strstr(bottom,"REMOTE WIFI"),"LCD ambient/PWM status");
    victus::renderLcd(LcdPage::Temperatures,screen,30000,top,bottom);
    require(std::strlen(top)==16 && std::strstr(top,"CPU:130C") &&
            std::strstr(top,"GPU:120C") && std::strstr(bottom,"30s"),"LCD thermal formatting");
    screen.telemetryFresh=false;
    victus::renderLcd(LcdPage::Temperatures,screen,1000,top,bottom);
    require(std::strstr(top,"CPU:--C") && std::strstr(top,"GPU:--C"),"LCD no stale telemetry");
    screen.ambientC=NAN;
    victus::renderLcd(LcdPage::FanStatus,screen,0,top,bottom);
    require(std::strstr(top,"NTC Fault") && std::strstr(bottom,"REMOTE WIFI"),"LCD true sensor faults");
    // Regression: 18 Hz with 8-bit LEDC caused ledcAttach() to fail and
    // abort() before Wi-Fi/TCP could start. ESP32-C3 requires 14-bit timing.
    require(board::PwmFrequencyHz==18&&board::PwmResolutionBits==14,"18 Hz / 14-bit LEDC profile");
    require(board::LedcMaximumDuty==16383,"14-bit duty range");
    require(board::LedcDutyFrom8Bit(0)==0&&board::LedcDutyFrom8Bit(255)==16383,"LEDC endpoints");
    require(board::LedcDutyFrom8Bit(225)==14456,"NOT: logical 30 -> electrical 225 -> LEDC");
    require(board::LedcDutyFrom8Bit(0)==0,"NOT: logical 255 -> electrical zero");
    require(board::LedcDutyFrom8Bit(128)==8224,"LEDC midpoint rounding");
    require(board::LedcDutyFrom8Bit(300)==16383,"out-of-range duty saturation");
    for(uint32_t v=1;v<=255;++v)
        require(board::LedcDutyFrom8Bit(v)>board::LedcDutyFrom8Bit(v-1),"strictly increasing LEDC scale");
    auto c=config();require(c.valid(),"default config");
    require(c.evaluate(0)==30&&c.evaluate(100)==255&&c.evaluate(50)==130,"curve interpolation");
    auto bad=c;bad.points[2].input=25;require(!bad.valid(),"duplicate input");
    bad=c;bad.points[2].output=29;require(!bad.valid(),"below min");
    bad=c;bad.points[4].output=256;require(!bad.valid(),"above max");
    bad=c;bad.points[3].output=100;require(!bad.valid(),"decreasing output");
    bad=c;bad.requestMaximum=bad.requestMinimum;require(!bad.valid(),"bad request calibration");
    bad=c;for(auto& ch:bad.revision)ch='a';require(!bad.valid(),"unterminated persisted revision");
    require(victus::ExternalController::electrical(30)==225&&victus::ExternalController::electrical(255)==0,"NOT 8-bit polarity");
    require(board::LedcDutyFrom8Bit(victus::ExternalController::electrical(30))==14456
         && board::LedcDutyFrom8Bit(victus::ExternalController::electrical(255))==0,
            "physical LEDC 14-bit polarity");
    victus::ExternalController fan;fan.configure(c);
    fan.receive(true,27,0,false,0);require(fan.tick(0,24)==255,"startup pulse");
    require(fan.tick(799,24)==255,"startup hold");
    int previous=255;
    for(uint32_t t=800;t<30000;t+=20) {
        if(t%1000==0)fan.receive(true,27,0,false,t);
        int duty=fan.tick(t,24);require(duty<=previous&&duty>=30,"monotone ramp down");require(previous-duty<=1,"bounded fall step");previous=duty;
    }
    require(previous<=34,"settles near floor inside hysteresis");
    fan.receive(true,27,100,false,30000);
    previous=fan.tick(30000,24);
    for(uint32_t t=30020;t<=40000;t+=20) {
        if(t%1000==0)fan.receive(true,27,100,false,t);
        int duty=fan.tick(t,24);require(duty>=previous&&duty-previous<=1,"bounded monotone rise");previous=duty;
    }
    require(previous>=250,"settles near maximum");
    require(fan.remote(),"remote active");fan.tick(41999,24);require(fan.remote(),"lease before deadline");
    fan.tick(42000,24);require(!fan.remote(),"lease exactly 2s");
    fan.receive(true,27,50,false,43000);require(fan.tick(43000,NAN)==255&&!fan.remote(),"sensor fault immediate max");
    fan.receive(true,27,0,false,44000);require(fan.tick(44000,45)==255,"hot ambient bypass");
    fan.receive(false,27,50,false,45000);fan.tick(45000,24);require(!fan.remote(),"MonitorOnly cannot grant control");
    fan.receive(true,27,NAN,false,45020);fan.tick(45020,24);require(!fan.remote(),"invalid demand");
    victus::ExternalController floor;floor.configure(c);
    for(uint32_t t=0;t<30000;t+=20){if(t%1000==0)floor.receive(true,27,0,false,t);floor.tick(t,24);}
    require(floor.tick(30000,40)>=143,"ambient floor bypasses inertia");
    floor.receive(false,27,0,false,30020);require(floor.tick(30020,24)>=89,"Local minimum bypasses old remote low duty");
    c.input=1;fan.configure(c); // same revision is immutable and ignored by the controller
    std::strcpy(c.revision,"1123456789abcdef0123456789abcdef");fan.configure(c);
    fan.receive(true,65535,50,false,46000);fan.tick(46000,24);require(!fan.remote(),"out of explicit calibration");
    fan.receive(true,27,-1,true,47000);fan.tick(47000,24);require(fan.remote(),"request input supports Manual without demand");
    fan.receive(true,27,50,false,UINT32_MAX-1000);fan.tick(998,24);require(fan.remote(),"millis wrap live");
    fan.tick(999,24);require(!fan.remote(),"millis wrap deadline");
    victus::AmbientFilter ntc;require(ntc.update(25)==25&&ntc.update(29)==26,"NTC filter");
    require(std::isnan(ntc.update(NAN))&&ntc.update(24)==24,"NTC fault not masked");
    require(ntc.update(45)==45,"hot NTC bypasses smoothing");
    std::cout<<"PASS external controller: curve bounds, interpolation, startup, inertia, hysteresis, ramp rates, sensor fault, hot ambient, lease, authority, calibration and rollover\n";
    return 0;
}
