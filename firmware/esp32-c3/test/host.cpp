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
        victus::ControlPolicy policy;int pwm;
        if(!policy.map(27,pwm)||pwm!=50||policy.map(55,pwm)||policy.map(-1,pwm)||
           victus::ControlPolicy::electricalDuty(0,true)!=255||victus::ControlPolicy::electricalDuty(100,true)!=0||
           victus::ControlPolicy::electricalDuty(50,true)!=127||victus::ControlPolicy::local(NAN)!=100) return 1;
        victus::Lease lease;
        lease.command(true,27,1000);if(lease.tick(2999,24)!=50||!lease.remote())return 2;
        if(lease.tick(3000,24)!=35||lease.remote())return 3;
        lease.command(true,27,UINT32_MAX-1000);if(lease.tick(998,24)!=50||lease.tick(999,24)!=35)return 4;
        lease.command(true,27,5000);if(lease.tick(5001,NAN)!=100||lease.remote())return 5;
        lease.command(true,55,6000);if(lease.tick(6001,24)!=35||lease.remote())return 6;
        lease.command(true,27,7000);lease.disable();if(lease.tick(7001,24)!=35||lease.remote())return 7;
        std::cout<<"PASS policy, NOT inversion, independent 2000 ms lease, millis rollover, sensor fault, calibration rejection\n";return 0;
    }
    HostCrypto crypto;victus::Key key{};
    if(!victus::decodeKey("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",key))return 1;
    victus::Session session(key,crypto);victus::ExternalController controller;
    const auto start=std::chrono::steady_clock::now();
    auto now=[&] {return static_cast<uint32_t>(std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now()-start).count());};
    std::string line;
    while(std::getline(std::cin,line)) {
        std::string response;int duty=controller.tick(now(),24);
        session.appliedConfigRevision=controller.revision();session.appliedDuty=duty;session.externalRequest=controller.requested();
        if(!session.accept(line,now(),24,static_cast<int>(std::lround(duty*100.0/255)),controller.remote(),response)) {std::cout<<"REJECT"<<std::endl;return 2;}
        if(session.hasConfiguration)controller.configure(session.configuration);
        controller.receive(session.remoteRequest,session.latest.request,session.latest.demand,session.latest.manual,now());controller.tick(now(),24);
        std::cout<<response<<std::endl;session.sent(now());
    }
}
