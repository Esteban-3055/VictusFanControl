#include "VictusProtocol.h"
#include <algorithm>
#include <cmath>
#include <limits>

namespace victus {
namespace {
constexpr char alphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
bool integer(JsonVariantConst v, int64_t min, int64_t max) { return v.is<int64_t>() && v.as<int64_t>() >= min && v.as<int64_t>() <= max; }
bool number(JsonVariantConst v, double min, double max) {
    return v.isNull() || ((v.is<double>() || v.is<int64_t>()) && !v.is<bool>() && std::isfinite(v.as<double>()) && v.as<double>() >= min && v.as<double>() <= max);
}
bool choice(JsonVariantConst v, std::initializer_list<const char*> choices) {
    if (!v.is<const char*>()) return false;
    for (auto c : choices) if (v.as<std::string>() == c) return true;
    return false;
}
bool timestamp(JsonVariantConst v, int64_t& result) {
    if (!v.is<const char*>()) return false;
    auto s = v.as<std::string>(); if (s.size() < 20) return false;
    auto digits = [&](size_t at, size_t n) { int out = 0;
        if (at + n > s.size()) return -1;
        for (size_t i = at; i < at + n; ++i) { if (s[i] < '0' || s[i] > '9') return -1; out = out * 10 + s[i] - '0'; } return out; };
    int year = digits(0,4), month = digits(5,2), day = digits(8,2), hour = digits(11,2), minute = digits(14,2), second = digits(17,2);
    if (s[4]!='-' || s[7]!='-' || s[10]!='T' || s[13]!=':' || s[16]!=':' || year < 1 || month < 1 || month > 12 ||
        day < 1 || hour < 0 || hour > 23 || minute < 0 || minute > 59 || second < 0 || second > 59) return false;
    bool leap = year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);
    const int days[] = {31,28,31,30,31,30,31,31,30,31,30,31};
    if (day > days[month-1] + (month == 2 && leap ? 1 : 0)) return false;
    auto before = [](int y) -> int64_t { --y; return 365LL*y + y/4 - y/100 + y/400; };
    int64_t count = before(year) - before(1970) + day - 1;
    for (int i = 1; i < month; ++i) count += days[i-1] + (i == 2 && leap ? 1 : 0);
    size_t pos = 19; int ms = 0;
    if (s[pos] == '.') { ++pos; size_t start = pos;
        while (pos < s.size() && s[pos] >= '0' && s[pos] <= '9') { if (pos-start < 3) ms = ms*10+s[pos]-'0'; ++pos; }
        if (pos == start || pos-start > 9) return false;
        for (size_t n = pos-start; n < 3; ++n) ms *= 10;
    }
    int offset = 0;
    if (pos < s.size() && s[pos] == 'Z') { if (++pos != s.size()) return false; }
    else {
        if (pos + 6 != s.size() || (s[pos]!='+' && s[pos]!='-') || s[pos+3]!=':') return false;
        int oh = digits(pos+1,2), om = digits(pos+4,2);
        if (oh < 0 || oh > 14 || om < 0 || om > 59 || (oh == 14 && om != 0)) return false;
        offset = (oh*60+om)*(s[pos]=='+' ? 1 : -1);
    }
    result = ((count*24+hour)*60+minute-offset)*60000 + second*1000 + ms; return true;
}
std::string quoted(const std::string& s) { StaticJsonDocument<512> d; d.set(s); std::string out; serializeJson(d,out); return out; }
}
std::string base64(const Key& key) {
    std::string out; unsigned bits = 0; int n = 0;
    for (auto b : key) { bits = (bits << 8) | b; n += 8; while (n >= 6) { n -= 6; out += alphabet[(bits >> n) & 63]; } }
    if (n) out += alphabet[(bits << (6-n)) & 63];
    while (out.size() % 4) out += '='; return out;
}
bool decodeKey(const std::string& text, Key& key) {
    if (text.size() != 44 || text.back() != '=') return false;
    unsigned bits = 0; int n = 0; size_t pos = 0;
    for (size_t i = 0; i < 43; ++i) {
        auto p = strchr(alphabet, text[i]); if (!p || text[i] == '\0') return false;
        bits = (bits << 6) | static_cast<unsigned>(p-alphabet); n += 6;
        if (n >= 8) { n -= 8; if (pos == key.size()) return false; key[pos++] = static_cast<uint8_t>(bits >> n); }
    }
    return pos == 32 && base64(key) == text;
}
bool equalProof(const std::string& a, const std::string& b) {
    Key x{}, y{}; if (!decodeKey(a,x) || !decodeKey(b,y)) return false;
    volatile uint8_t difference = 0;
    for (size_t i=0; i<32; ++i) difference = difference | (x[i]^y[i]);
    return difference == 0;
}
Session::Session(const Key& key,Crypto& c,const ExternalConfig* previousConfiguration): pairing(key),crypto(c) {
    // Configuration survives TCP reconnects; authentication, telemetry and the
    // remote lease never do. Reused revisions remain immutable across sessions.
    if(previousConfiguration&&previousConfiguration->valid()) {
        configuration=*previousConfiguration;hasConfiguration=true;
    }
}
Session::~Session() {
    for (auto* k : {&pairing,&receiveKey,&sendKey}) { volatile uint8_t* p = k->data(); for (size_t i=0;i<32;++i) p[i]=0; }
}
std::string Session::proof(const std::string& role) {
    return base64(crypto.hmac(pairing,"VictusExternalCooling/v1/auth\n"+role+"\n"+id+"\n"+clientNonce+"\n"+serverNonce));
}
std::string Session::frame(const std::string& type,int64_t seq,const std::string& payload,bool sign) {
    std::string mac;
    if (sign) mac=base64(crypto.hmac(sendKey,"VictusExternalCooling/v1/frame\n"+type+"\n"+id+"\n"+std::to_string(seq)+"\n"+payload));
    return "{\"protocolVersion\":1,\"type\":"+quoted(type)+",\"sessionId\":"+quoted(id)+",\"sequence\":"+std::to_string(seq)+",\"payload\":"+payload+",\"mac\":"+quoted(mac)+"}";
}
bool Session::accept(const std::string& line,uint32_t now,double ambient,int applied,bool remote,std::string& response) {
    remoteRequest=false; response.clear(); std::string raw;
    if (!validateJson(line,raw)) return false;
    DynamicJsonDocument document(12288);
    if (deserializeJson(document,line,DeserializationOption::NestingLimit(8))) return false;
    auto root=document.as<JsonObjectConst>();
    for (auto k : {"protocolVersion","type","sessionId","sequence","payload","mac"}) if (!root.containsKey(k)) return false;
    if (!integer(root["protocolVersion"],1,1) || !integer(root["sequence"],0,INT64_MAX) ||
        !root["type"].is<const char*>() || !root["sessionId"].is<const char*>() || !root["mac"].is<const char*>() || !root["payload"].is<JsonObjectConst>()) return false;
    auto session=root["sessionId"].as<std::string>(); if (session.size()!=32) return false;
    for (char c:session) if (!((c>='0'&&c<='9')||(c>='a'&&c<='f'))) return false;
    auto type=root["type"].as<std::string>(); auto sequence=root["sequence"].as<int64_t>(); auto mac=root["mac"].as<std::string>(); auto p=root["payload"].as<JsonObjectConst>();
    if (phase==0) {
        if (type!="hello" || sequence!=0 || !mac.empty() || !p["clientNonce"].is<const char*>()) return false;
        id=session; clientNonce=p["clientNonce"].as<std::string>(); Key nonce{}; if (!decodeKey(clientNonce,nonce)) return false;
        serverNonce=base64(crypto.random()); phase=1;
        response=frame("challenge",0,"{\"serverNonce\":"+quoted(serverNonce)+",\"proof\":"+quoted(proof("server"))+"}",false); return true;
    }
    if (session!=id) return false;
    if (phase==1) {
        if (type!="authenticate" || sequence!=0 || !mac.empty() || !p["proof"].is<const char*>() || !equalProof(proof("client"),p["proof"].as<std::string>())) return false;
        if (!decodeKey(proof("client-to-device"),receiveKey) || !decodeKey(proof("device-to-client"),sendKey)) return false;
        response=frame("authenticated",0,"{\"proof\":"+quoted(proof("ready"))+"}",false); phase=2; return true;
    }
    if (type!="telemetry" || sequence<=previous || !equalProof(mac,base64(crypto.hmac(receiveKey,
        "VictusExternalCooling/v1/frame\ntelemetry\n"+id+"\n"+std::to_string(sequence)+"\n"+raw))) || !telemetry(p,now)) return false;
    previous=sequence; if (outgoing==INT64_MAX) return false;
    StaticJsonDocument<1024> status;
    status["ackSequence"]=sequence;
    if (std::isfinite(ambient) && ambient>=-40 && ambient<=100) status["ambientTempC"]=ambient; else status["ambientTempC"]=nullptr;
    if (applied>=0&&applied<=100) status["appliedPwmPct"]=applied; else status["appliedPwmPct"]=nullptr;
    // Sensor availability is expressed by ambientTempC=null, not a TCP fault.
    status["mode"]=remote?"Remote":"Local"; status["communication"]="Ready";
    status["externalCurveVersion"]=1;status["externalMinimumOutput"]=0;
    if(appliedConfigRevision.empty()) status["appliedConfigRevision"]=nullptr; else status["appliedConfigRevision"]=appliedConfigRevision;
    if(appliedDuty>=0&&appliedDuty<=255) status["appliedDuty"]=appliedDuty; else status["appliedDuty"]=nullptr;
    if(externalRequest>=0&&externalRequest<=255) status["externalRequest"]=externalRequest; else status["externalRequest"]=nullptr;
    auto extensions=status.createNestedObject("extensions"); extensions["slot1"]=nullptr; extensions["slot2"]=nullptr;
    std::string payload; serializeJson(status,payload); response=frame("deviceStatus",++outgoing,payload,true); return true;
}
void Session::sent(uint32_t now) { if (phase==2 && outgoing>0) { replies[replyIndex]={outgoing,now}; replyIndex=(replyIndex+1)%16; } }
bool Session::telemetry(JsonObjectConst p,uint32_t now) {
    for (auto k : {"validForMs","replyToDeviceSequence","capturedAtUtc","sampledAtUtc","decisionAtUtc","cpuTempSampledAtUtc","gpuTempSampledAtUtc","ramSampledAtUtc",
        "cpuUsagePct","gpuUsagePct","ramUsagePct","demandaPct","solicitud","solicitudValida","cpuTargetLevel","gpuTargetLevel","modoSolicitado","modoEfectivo","cpuTempC","gpuTempC","powerSource","requestEvidence"}) if (!p.containsKey(k)) return false;
    if (!integer(p["validForMs"],2000,2000) || !p["solicitudValida"].is<bool>() ||
        !choice(p["modoSolicitado"],{"Firmware","Manual","Automatic"}) || !choice(p["modoEfectivo"],{"Firmware","Manual","Automatic","Transition","Interrupted","Unavailable"}) ||
        !choice(p["powerSource"],{"AC","Battery","Unknown"}) || !choice(p["requestEvidence"],{"WmiAcceptedSupervised","Unavailable","MonitorOnly","AwaitingDevice"})) return false;
    for (auto k : {"cpuUsagePct","gpuUsagePct","ramUsagePct","demandaPct"}) if (!number(p[k],0,100)) return false;
    for (auto k : {"cpuTempC","gpuTempC"}) if (!number(p[k],-20,130)) return false;
    for (auto k : {"solicitud","cpuTargetLevel","gpuTargetLevel"}) if (!p[k].isNull() && !integer(p[k],0,65535)) return false;
    if (!p["replyToDeviceSequence"].isNull() && !integer(p["replyToDeviceSequence"],1,INT64_MAX)) return false;
    int64_t captured; if (!timestamp(p["capturedAtUtc"],captured) || captured<=-62135596800000LL) return false;
    for (auto k : {"sampledAtUtc","decisionAtUtc","cpuTempSampledAtUtc","gpuTempSampledAtUtc","ramSampledAtUtc"}) {
        int64_t at; if (!p[k].isNull() && (!timestamp(p[k],at) || at>captured)) return false;
    }
    bool valid=p["solicitudValida"].as<bool>(); auto mode=p["modoSolicitado"].as<std::string>();
    if (valid==p["solicitud"].isNull() || (!valid&&(!p["cpuTargetLevel"].isNull()||!p["gpuTargetLevel"].isNull())) || (mode!="Automatic"&&!p["demandaPct"].isNull())) return false;
    if (valid) {
        if ((mode!="Manual"&&mode!="Automatic") || mode!=p["modoEfectivo"].as<std::string>() || p["cpuTargetLevel"].isNull() || p["gpuTargetLevel"].isNull() ||
            p["replyToDeviceSequence"].isNull() || p["requestEvidence"].as<std::string>()!="WmiAcceptedSupervised" || p["cpuTempC"].isNull() || p["gpuTempC"].isNull()) return false;
        for (auto k : {"sampledAtUtc","cpuTempSampledAtUtc","gpuTempSampledAtUtc"}) { int64_t at; if (!timestamp(p[k],at) || captured-at>=5000) return false; }
        if (mode=="Automatic") { int64_t at; if (p["demandaPct"].isNull() || !timestamp(p["decisionAtUtc"],at) || captured-at>=5000) return false; }
    }
    ExternalConfig candidate;
    bool configPresent=!p["externalConfiguration"].isNull();
    if(configPresent) {
        auto c=p["externalConfiguration"].as<JsonObjectConst>();
        if(c.isNull()||!integer(c["schemaVersion"],1,1)||!c["revision"].is<const char*>())return false;
        auto revision=c["revision"].as<std::string>();if(revision.size()!=32)return false;
        std::copy(revision.begin(),revision.end(),candidate.revision);
        struct Field {const char* name;int* value;int min,max;};
        for(auto f:{Field{"input",&candidate.input,0,1},{"requestMinimum",&candidate.requestMinimum,0,65534},
            {"requestMaximum",&candidate.requestMaximum,1,65535},{"manualOutput",&candidate.manualOutput,0,255},
            {"rise",&candidate.rise,1,100},{"fall",&candidate.fall,1,100},{"filterMs",&candidate.filterMs,0,10000},
            {"hysteresis",&candidate.hysteresis,0,30},{"downHoldMs",&candidate.downHoldMs,0,30000},
            {"startMs",&candidate.startMs,0,3000},{"startOutput",&candidate.startOutput,0,255}}) {
            if(!integer(c[f.name],f.min,f.max))return false;*f.value=c[f.name].as<int>();
        }
        auto points=c["points"].as<JsonArrayConst>();
        if(points.isNull()||points.size()<2||points.size()>16)return false;
        candidate.count=static_cast<int>(points.size());int i=0;
        for(auto point:points) {
            if(!integer(point["input"],0,100)||!integer(point["output"],0,255))return false;
            candidate.points[i++]={point["input"].as<int>(),point["output"].as<int>()};
        }
        if(!candidate.valid())return false;
        // Includes the retained configuration from before this authenticated session.
        if(hasConfiguration&&std::string(configuration.revision)==candidate.revision) {
            if(configuration.count!=candidate.count||configuration.input!=candidate.input||configuration.requestMinimum!=candidate.requestMinimum||
                configuration.requestMaximum!=candidate.requestMaximum||configuration.manualOutput!=candidate.manualOutput||configuration.rise!=candidate.rise||
                configuration.fall!=candidate.fall||configuration.filterMs!=candidate.filterMs||configuration.hysteresis!=candidate.hysteresis||
                configuration.downHoldMs!=candidate.downHoldMs||configuration.startMs!=candidate.startMs||configuration.startOutput!=candidate.startOutput)return false;
            for(int j=0;j<candidate.count;++j)if(configuration.points[j].input!=candidate.points[j].input||configuration.points[j].output!=candidate.points[j].output)return false;
        }
    }
    if(configPresent) {configuration=candidate;hasConfiguration=true;}
    auto age=[&](const char* field)->uint32_t {
        int64_t at;
        return timestamp(p[field],at) ? static_cast<uint32_t>(std::min<int64_t>(5000,captured-at)) : 5000;
    };
    latest.received=true;latest.receivedAt=now;
    latest.usageAge=age("sampledAtUtc");latest.ramAge=age("ramSampledAtUtc");
    latest.demandAge=age("decisionAtUtc");
    latest.cpuTempAge=age("cpuTempSampledAtUtc");latest.gpuTempAge=age("gpuTempSampledAtUtc");
    latest.manual=mode=="Manual";
    latest.cpu=p["cpuUsagePct"].isNull()?-1:p["cpuUsagePct"].as<double>(); latest.gpu=p["gpuUsagePct"].isNull()?-1:p["gpuUsagePct"].as<double>();
    latest.ram=p["ramUsagePct"].isNull()?-1:p["ramUsagePct"].as<double>(); latest.demand=p["demandaPct"].isNull()?-1:p["demandaPct"].as<double>();
    latest.cpuTempC=p["cpuTempC"].isNull()?NAN:p["cpuTempC"].as<double>();
    latest.gpuTempC=p["gpuTempC"].isNull()?NAN:p["gpuTempC"].as<double>();
    latest.request=valid?p["solicitud"].as<int>():-1; latest.valid=valid; latest.source=p["powerSource"].as<std::string>();
    if (valid) for (auto stamp:replies) if (stamp.sequence==p["replyToDeviceSequence"].as<int64_t>() && static_cast<uint32_t>(now-stamp.at)<2000) remoteRequest=true;
    return true;
}
}
