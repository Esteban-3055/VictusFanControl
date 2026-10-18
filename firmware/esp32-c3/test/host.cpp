// Host-only protocol harness: public fixture key, fake ambient/PWM, zero hardware IO.
#include "VictusProtocol.h"
#include <chrono>
#include <iostream>
#ifdef _WIN32
#include <windows.h>
#include <bcrypt.h>
#else
#include <openssl/hmac.h>
#endif

struct HostCrypto : victus::Crypto {
    victus::Key hmac(const victus::Key& key,const std::string& message) override {
        victus::Key out{};
#ifdef _WIN32
        BCRYPT_ALG_HANDLE algorithm=nullptr;
        if (BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,BCRYPT_ALG_HANDLE_HMAC_FLAG)<0) std::abort();
        auto status=BCryptHash(algorithm,const_cast<PUCHAR>(key.data()),32,
            reinterpret_cast<PUCHAR>(const_cast<char*>(message.data())),static_cast<ULONG>(message.size()),out.data(),32);
        BCryptCloseAlgorithmProvider(algorithm,0); if (status<0) std::abort();
#else
        unsigned size=32;
        if(!HMAC(EVP_sha256(),key.data(),32,reinterpret_cast<const unsigned char*>(message.data()),message.size(),out.data(),&size)) std::abort();
#endif
        return out;
    }
    victus::Key random() override { victus::Key nonce{};for(size_t i=0;i<32;++i)nonce[i]=static_cast<uint8_t>(64+i);return nonce; }
};
int controllerTests();
int main(int argc,char** argv) {
    if(argc==2&&std::string(argv[1])=="--controller")return controllerTests();
    if(argc==2 && std::string(argv[1])=="--policy") {
        victus::ExternalController policy;
        if(policy.electrical(0)!=255||policy.electrical(255)!=0||policy.tick(0,NAN)!=0)return 1;
        policy.receive(true,27,50,false,1000);policy.tick(2999,NAN);if(!policy.remote())return 2;
        policy.tick(3000,45);if(policy.remote())return 3;
        policy.receive(true,27,50,false,UINT32_MAX-1000);policy.tick(998,NAN);if(!policy.remote())return 4;
        policy.tick(999,24);if(policy.remote())return 5;
        policy.receive(true,55,50,false,6000);policy.tick(6001,24);if(policy.remote())return 6;
        std::cout<<"PASS actual controller, NOT inversion, 2000 ms lease, millis rollover, ambient isolation, legacy calibration rejection\n";return 0;
    }
    HostCrypto crypto;victus::Key key{};
    if(!victus::decodeKey("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",key))return 1;
    victus::Session session(key,crypto);victus::ExternalController controller;
    const auto start=std::chrono::steady_clock::now();
    auto now=[&] {return static_cast<uint32_t>(std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now()-start).count());};
    const double ambient=argc==2&&std::string(argv[1])=="--missing-ambient"?NAN:24;
    std::string line;
    while(std::getline(std::cin,line)) {
        std::string response;int duty=controller.tick(now(),ambient);
        session.appliedConfigRevision=controller.revision();session.appliedDuty=duty;session.externalRequest=controller.requested();
        if(!session.accept(line,now(),ambient,static_cast<int>(std::lround(duty*100.0/255)),controller.remote(),response)) {std::cout<<"REJECT"<<std::endl;return 2;}
        if(argc==2 && std::string(argv[1])=="--readings" && session.latest.received) {
            // Test-only side channel; the signed response is unchanged.
            const auto& m=session.latest;
            StaticJsonDocument<512> snapshot;
            auto put=[&](const char* name,double value,uint32_t age) {
                double v=m.fresh(value,age,m.receivedAt+1000);
                if(std::isfinite(v) && (std::string(name).find("Temp")!=std::string::npos || v>=0)) snapshot[name]=v;else snapshot[name]=nullptr;
            };
            put("cpuTemp",m.cpuTempC,m.cpuTempAge);put("gpuTemp",m.gpuTempC,m.gpuTempAge);
            put("cpu",m.cpu,m.usageAge);put("gpu",m.gpu,m.usageAge);
            put("ram",m.ram,m.ramAge);put("demand",m.demand,m.demandAge);
            std::string value;serializeJson(snapshot,value);std::cerr<<value<<std::endl;
        }
        if(session.hasConfiguration)controller.configure(session.configuration);
        controller.receive(session.remoteRequest,session.latest.request,session.latest.demand,session.latest.manual,now());controller.tick(now(),ambient);
        if(argc==2 && std::string(argv[1])=="--configuration" && session.hasConfiguration) {
            // Test-only receipt evidence, after configure(). No production wire extension.
            const auto& c=session.configuration;
            StaticJsonDocument<2048> snapshot;
            snapshot["schemaVersion"]=1;snapshot["revision"]=controller.revision();
            snapshot["input"]=c.input;snapshot["requestMinimum"]=c.requestMinimum;snapshot["requestMaximum"]=c.requestMaximum;
            snapshot["manualOutput"]=c.manualOutput;snapshot["rise"]=c.rise;snapshot["fall"]=c.fall;
            snapshot["filterMs"]=c.filterMs;snapshot["hysteresis"]=c.hysteresis;snapshot["downHoldMs"]=c.downHoldMs;
            snapshot["startMs"]=c.startMs;snapshot["startOutput"]=c.startOutput;
            auto points=snapshot.createNestedArray("points");
            for(int i=0;i<c.count;++i) { auto p=points.createNestedObject();p["input"]=c.points[i].input;p["output"]=c.points[i].output; }
            std::string value;serializeJson(snapshot,value);std::cerr<<value<<std::endl;
        }
        std::cout<<response<<std::endl;session.sent(now());
    }
}
