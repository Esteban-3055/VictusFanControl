#include "ExternalController.h"
#include <cstring>
namespace victus {
bool ExternalConfig::valid() const {
    if (revision[32]!=0 || std::strlen(revision)!=32 || count<2||count>16||input<0||input>1||requestMinimum<0||requestMaximum>65535||requestMinimum>=requestMaximum||
        manualOutput<0||manualOutput>255||rise<1||rise>100||fall<1||fall>100||filterMs<0||filterMs>10000||hysteresis<0||hysteresis>30||
        downHoldMs<0||downHoldMs>30000||startMs<0||startMs>3000||startOutput<0||startOutput>255) return false;
    for (int i=0;i<32;++i) if (!((revision[i]>='0'&&revision[i]<='9')||(revision[i]>='a'&&revision[i]<='f'))) return false;
    for (int i=0;i<count;++i) if(points[i].input<0||points[i].input>100||points[i].output<0||points[i].output>255||
        (i>0&&(points[i].input<=points[i-1].input||points[i].output<points[i-1].output))) return false;
    return points[0].input==0&&points[count-1].input==100;
}
double ExternalConfig::evaluate(double x) const {
    x=std::clamp(x,0.0,100.0);
    for(int i=1;i<count;++i) if(x<=points[i].input) {
        auto a=points[i-1],b=points[i];return a.output+(b.output-a.output)*(x-a.input)/(b.input-a.input);
    }
    return points[count-1].output;
}
void ExternalController::configure(const ExternalConfig& value) {
    if (!value.valid() || (configured&&std::strcmp(config.revision,value.revision)==0)) return;
    config=value;configured=true;filterReady=false;falling=false;
}
void ExternalController::receive(bool allowed,int request,double demand,bool manual,uint32_t now) {
    active=allowed&&request>=0&&request<=65535;
    if (active) {
        if(configured) {
            if(config.input==1) {
                active=request>=config.requestMinimum&&request<=config.requestMaximum;
                command=config.evaluate(100.0*(request-config.requestMinimum)/(config.requestMaximum-config.requestMinimum));
            } else if(manual) command=config.manualOutput;
            else { active=std::isfinite(demand)&&demand>=0&&demand<=100;command=config.evaluate(demand); }
        } else {
            // Explicit legacy device calibration, never a protocol-wide notebook limit.
            active=request<=54;command=std::max(30.0,255.0*request/54);
        }
        if(active) admitted=now;
    }
}
int ExternalController::tick(uint32_t now,double ambient) {
    const bool first=!initialized;
    if(first) { initialized=true;previous=startedAt=now; }
    double dt=std::min(0.1,static_cast<uint32_t>(now-previous)/1000.0);previous=now;
    if(static_cast<uint32_t>(now-admitted)>=2000) active=false;
    if(!std::isfinite(ambient)||ambient < -40||ambient > 100) { active=false;duty=target=255;filterReady=false;falling=false;return 255; }
    double desired=active?command:std::clamp(89.0+(ambient-25)*8.3,89.0,255.0);
    // A high ambient temperature is an independent local floor; a cool NTC is not a notebook load sensor.
    const double ambientFloor=ambient>35?std::clamp(30.0+(ambient-35)*22.5,30.0,255.0):0.0;
    desired=std::max(desired,ambientFloor);
    const bool stopRequested=active&&configured&&command==0&&ambientFloor==0;
    if(!filterReady) {filtered=desired;filterReady=true;}
    else filtered+=(desired-filtered)*(config.filterMs==0?1.0:dt/(config.filterMs/1000.0+dt));
    // EMA approaches zero asymptotically; hysteresis may otherwise hold a
    // residual duty forever. Anchor an explicit stop to exactly zero only
    // inside the settling band, with the same sustained descent confirmation.
    double nextTarget=stopRequested&&filtered<=std::max(0.5,static_cast<double>(config.hysteresis))?0:filtered;
    if(nextTarget>=target+config.hysteresis) {target=nextTarget;falling=false;}
    else if(nextTarget<=target-config.hysteresis || (stopRequested&&nextTarget==0&&target>0)) {
        if(!falling) {falling=true;fallingAt=now;}
        if(static_cast<uint32_t>(now-fallingAt)>=static_cast<uint32_t>(config.downHoldMs)) target=nextTarget;
    } else falling=false;
    if(first) duty=config.startOutput;
    // Restart assistance after an actual stop, not on network reconnection.
    if(!first&&active&&duty<0.5&&desired>0&&target==0) {
        // A demand smaller than the hysteresis must still leave the stopped
        // state. Reset only this stopped -> running transition, not each tick.
        target=filtered=desired;falling=false;
        if(config.startMs>0&&config.startOutput>0) {
            startedAt=now;duty=std::max(duty,static_cast<double>(config.startOutput));
        }
    }
    if(static_cast<uint32_t>(now-startedAt)<static_cast<uint32_t>(config.startMs)) duty=std::max<double>(duty,config.startOutput);
    else duty+=std::clamp(target-duty,-config.fall*2.55*dt,config.rise*2.55*dt);
    // Sensor fault and very hot ambient bypass inertia. No tachometer: this is duty, not proven rotation.
    if(ambient>=45) duty=target=255;
    duty=std::max(duty,ambientFloor);
    if(!active)duty=std::max(duty,89.0);
    return std::clamp(static_cast<int>(std::lround(duty)),0,255);
}
}
