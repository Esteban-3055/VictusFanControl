#include <Arduino.h>
#include <WiFi.h>
#include <ESPmDNS.h>
#include <Preferences.h>
#include <Wire.h>
#include <mbedtls/md.h>
#include <esp_system.h>
#include <lwip/sockets.h>
#include <fcntl.h>
#include <atomic>
#include <memory>
#include "BoardConfig.h"
#include "VictusProtocol.h"

namespace {
struct EspCrypto : victus::Crypto {
    victus::Key hmac(const victus::Key& key,const std::string& input) override {
        victus::Key out{};
        if (mbedtls_md_hmac(mbedtls_md_info_from_type(MBEDTLS_MD_SHA256),key.data(),key.size(),
            reinterpret_cast<const uint8_t*>(input.data()),input.size(),out.data())!=0) abort();
        return out;
    }
    victus::Key random() override { victus::Key out{}; esp_fill_random(out.data(),out.size()); return out; }
} crypto;
Preferences preferences;
victus::Key pairing{};
std::unique_ptr<victus::Session> session;
int listener=-1,peer=-1;
std::string input,output; size_t sentBytes=0;
uint32_t frameAt=0,acceptedAt=0,lastFrameAt=0,writeAt=0,nextConnect=0,nextAccept=0;
bool reading=false,configured=false,networkReady=false;
unsigned wifiFailures=0;
std::atomic<float> ambient{NAN};
std::atomic<uint32_t> ambientAt{0};
std::atomic<int> applied{100};
std::atomic<bool> remote{false};
struct Command { bool valid; int request; uint32_t at; };
QueueHandle_t commands;
String ssid,password;
std::string serialLine,stagedSsid,stagedPassword;
int provision=0;
bool overflow=false;
float currentAmbient(uint32_t now) { return static_cast<uint32_t>(now-ambientAt.load())<3000 ? ambient.load() : NAN; }

void controlTask(void*) {
    victus::Lease lease({board::RequestMinimum,board::RequestMaximum,board::MinimumRunningPwm});
    for (;;) {
        Command command{};
        if (xQueueReceive(commands,&command,0)==pdTRUE) lease.command(command.valid,command.request,command.at);
        int pwm=lease.tick(millis(),currentAmbient(millis()));
        if (board::EnablePwm) ledcWrite(0,victus::ControlPolicy::electricalDuty(pwm,board::PwmActiveLow));
        applied.store(pwm); remote.store(lease.remote());
        vTaskDelay(pdMS_TO_TICKS(20));
    }
}
void local() { Command c{false,0,millis()}; xQueueOverwrite(commands,&c); }
void disconnectPeer() {
    local(); if (peer>=0) { close(peer); peer=-1; }
    session.reset(); input.clear(); output.clear(); sentBytes=0; reading=false;
}
void stopNetwork() {
    disconnectPeer(); if (listener>=0) { close(listener); listener=-1; }
    if (networkReady) MDNS.end(); networkReady=false;
}
void startServer() {
    listener=socket(AF_INET,SOCK_STREAM,IPPROTO_TCP); if (listener<0) return;
    int yes=1; setsockopt(listener,SOL_SOCKET,SO_REUSEADDR,&yes,sizeof(yes));
    sockaddr_in address{}; address.sin_family=AF_INET; address.sin_port=htons(board::Port); address.sin_addr.s_addr=INADDR_ANY;
    if (bind(listener,reinterpret_cast<sockaddr*>(&address),sizeof(address))!=0 || listen(listener,1)!=0) { close(listener);listener=-1;return; }
    fcntl(listener,F_SETFL,O_NONBLOCK);
    if (MDNS.begin(board::Hostname)) MDNS.addService("victus-base","tcp",board::Port);
    networkReady=true; Serial.printf("Servidor TCP %s:%u disponible.\n",WiFi.localIP().toString().c_str(),board::Port);
}
void network(uint32_t now) {
    if (!configured || provision) return;
    if (WiFi.status()!=WL_CONNECTED) {
        if (networkReady || listener>=0 || peer>=0) stopNetwork();
        if (static_cast<int32_t>(now-nextConnect)>=0) {
            WiFi.begin(ssid.c_str(),password.c_str());
            nextConnect=now+std::min(30000u,1000u<<(std::min(++wifiFailures,5u)));
        }
        return;
    }
    wifiFailures=0;
    if (listener<0) { if (static_cast<int32_t>(now-nextConnect)>=0) { startServer(); nextConnect=now+5000; } return; }
    int incoming=accept(listener,nullptr,nullptr);
    if (incoming>=0) {
        if (peer>=0 || static_cast<int32_t>(now-nextAccept)<0) close(incoming);
        else {
            peer=incoming; fcntl(peer,F_SETFL,O_NONBLOCK); int yes=1;
            setsockopt(peer,IPPROTO_TCP,TCP_NODELAY,&yes,sizeof(yes));
            session.reset(new victus::Session(pairing,crypto)); acceptedAt=lastFrameAt=now;
        }
    }
    if (peer<0) return;
    if (reading && static_cast<uint32_t>(now-frameAt)>=5000 || static_cast<uint32_t>(now-lastFrameAt)>=5000 ||
        !session->authenticated() && static_cast<uint32_t>(now-acceptedAt)>=5000) { disconnectPeer();nextAccept=now+1000;return; }
    if (!output.empty()) {
        if (static_cast<uint32_t>(now-writeAt)>=3000) { disconnectPeer();return; }
        int n=send(peer,output.data()+sentBytes,output.size()-sentBytes,MSG_DONTWAIT);
        if (n>0) sentBytes+=static_cast<size_t>(n);
        else if (n<0 && errno!=EAGAIN && errno!=EWOULDBLOCK) { disconnectPeer();return; }
        if (sentBytes==output.size()) { session->sent(millis()); output.clear();sentBytes=0; }
        return;
    }
    // At most 512 bytes per loop: TCP floods cannot starve local control or provisioning.
    for (int budget=0;budget<512 && peer>=0;++budget) {
        char c; int n=recv(peer,&c,1,MSG_DONTWAIT);
        if (n==0) { disconnectPeer();return; }
        if (n<0) { if (errno!=EAGAIN && errno!=EWOULDBLOCK) disconnectPeer();return; }
        if (!reading) { reading=true;frameAt=now; }
        if (c=='\n') {
            std::string response;
            if (!session->accept(input,millis(),currentAmbient(millis()),board::EnablePwm?applied.load():-1,board::EnablePwm&&remote.load(),response)) {
                disconnectPeer();nextAccept=now+1000;Serial.println("Sesion rechazada o terminada.");return;
            }
            Command command{session->remoteRequest && board::EnablePwm,session->latest.request,millis()};
            xQueueOverwrite(commands,&command);
            input.clear();reading=false;lastFrameAt=millis();output=std::move(response)+"\n";writeAt=millis();return;
        }
        if (input.size()>=4096) { disconnectPeer();nextAccept=now+1000;return; }
        input+=c;
    }
}
void readAmbient() {
    unsigned raw=0,mv=0;
    for (int i=0;i<8;++i) { raw+=analogRead(board::NtcPin);mv+=analogReadMilliVolts(board::NtcPin); }
    raw/=8;mv/=8;
    if (raw<8 || raw>4087 || mv<20 || mv>2450) { ambient.store(NAN);ambientAt.store(millis());return; }
    double resistance=board::FixedResistorOhms*mv/(board::SupplyMilliVolts-mv);
    double kelvin=1.0/(1.0/298.15+log(resistance/board::NtcNominalOhms)/board::NtcBeta);
    double c=kelvin-273.15; ambient.store(std::isfinite(c)&&c>=-40&&c<=100 ? c : NAN);ambientAt.store(millis());
}
// PCF8574 mapping P0=RS,P1=RW,P2=E,P3=backlight,P4..7=data. No third-party LCD dependency.
bool lcd=false;
void lcdNibble(uint8_t data) {
    Wire.beginTransmission(board::LcdAddress);Wire.write(data|8);Wire.write(data|12);Wire.write(data|8);
    if (Wire.endTransmission()!=0) lcd=false;
}
void lcdByte(uint8_t value,bool text=false) { lcdNibble((value&0xF0)|(text?1:0));lcdNibble((value<<4)|(text?1:0)); }
void lcdRow(uint8_t address,const char* text) { lcdByte(address);for (int i=0;i<16;++i) lcdByte(text[i]?text[i]:' ',true); }
void display(uint32_t now) {
    if (!lcd) return;
    char first[17]={},second[17]={};
    if ((now/4000)%2 && session && session->authenticated()) {
        auto m=session->latest;
        auto percent=[](double v)->std::string { return v<0?"--":std::to_string(static_cast<int>(lround(v))); };
        snprintf(first,sizeof(first),"C:%s%% G:%s%%",percent(m.cpu).c_str(),percent(m.gpu).c_str());
        snprintf(second,sizeof(second),"R:%s D:%s S:%s",percent(m.ram).c_str(),percent(m.demand).c_str(),m.valid?std::to_string(m.request).c_str():"--");
    } else {
        auto a=currentAmbient(now);
        if (!board::EnablePwm) snprintf(first,sizeof(first),"PWM desactivado");
        else if (std::isfinite(a)) snprintf(first,sizeof(first),"Amb:%4.1fC %3d%%",a,applied.load());
        else snprintf(first,sizeof(first),"NTC Fault %3d%%",applied.load());
        snprintf(second,sizeof(second),"%s %s",board::EnablePwm?(remote.load()?"Remote":"Local"):"PWM off",WiFi.status()==WL_CONNECTED?"WiFi":"Offline");
    }
    // Pad explicitly; never read beyond a short C string.
    for (auto row:{first,second}) { size_t n=strlen(row);for (size_t i=n;i<16;++i) row[i]=' ';row[16]=0; }
    lcdRow(0x80,first);lcdRow(0xC0,second);
}
void wipe(std::string& s) { volatile char* p=s.empty()?nullptr:&s[0]; for(size_t i=0;i<s.size();++i)p[i]=0;s.clear(); }
void provisioningLine(std::string& line) {
    if (!provision) {
        if (line=="setup") { stopNetwork(); WiFi.disconnect(); provision=1;Serial.println("SSID (sin eco):"); }
        else if (line=="status") { Serial.printf("Configurado=%s PWM=%s Modo=%s\n",configured?"si":"no",board::EnablePwm?"activo":"desactivado",remote.load()?"Remote":"Local"); }
        else if (line=="erase CONFIRM") { stopNetwork();WiFi.disconnect(true,true);preferences.clear();configured=false;pairing.fill(0);ssid="";password="";Serial.println("Configuracion borrada."); }
        else Serial.println("Comandos USB: setup | status | erase CONFIRM");
    } else if (provision==1) {
        if (line.empty()||line.size()>32) {Serial.println("SSID invalido; reintenta.");return;}
        stagedSsid=line;provision=2;Serial.println("Clave WiFi (sin eco; vacia para red abierta):");
    } else if (provision==2) {
        if (!line.empty()&&(line.size()<8||line.size()>63)) {Serial.println("Clave WiFi invalida; reintenta.");return;}
        stagedPassword=line;provision=3;Serial.println("Clave compartida Base64 de 32 bytes (sin eco):");
    } else {
        victus::Key key{};
        if (!victus::decodeKey(line,key) || line=="AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=") {Serial.println("Clave invalida o vector publico; genera otra en Windows.");return;}
        preferences.putBool("valid",false);
        bool ok=preferences.putString("ssid",stagedSsid.c_str())>0 && preferences.putString("password",stagedPassword.c_str())>=stagedPassword.size() && preferences.putBytes("pairing",key.data(),key.size())==key.size();
        ok=ok && preferences.putBool("valid",true)>0;
        if (ok) {ssid=stagedSsid.c_str();password=stagedPassword.c_str();pairing=key;configured=true;provision=0;nextConnect=millis();Serial.println("Configuracion guardada. No se muestran secretos.");}
        else {configured=false;Serial.println("Error de guardado; repite setup.");provision=0;}
        key.fill(0);wipe(stagedSsid);wipe(stagedPassword);
    }
}
void serialInput() {
    for(int count=0;count<64&&Serial.available();++count) {
        char c=Serial.read();if(c=='\r')continue;
        if(c=='\n') { if(!overflow)provisioningLine(serialLine);else Serial.println("Entrada demasiado larga.");wipe(serialLine);overflow=false; }
        else if(c==8||c==127) {if(!serialLine.empty())serialLine.pop_back();}
        else if(serialLine.size()<128)serialLine+=c;else overflow=true;
    }
}
}
void setup() {
    Serial.begin(115200); commands=xQueueCreate(1,sizeof(Command));if(!commands)abort();
    if(board::EnablePwm) {
        pinMode(board::PwmPin,OUTPUT);digitalWrite(board::PwmPin,board::PwmActiveLow?LOW:HIGH);
        ledcSetup(0,board::PwmFrequencyHz,board::PwmResolutionBits);ledcWrite(0,victus::ControlPolicy::electricalDuty(100,board::PwmActiveLow));ledcAttachPin(board::PwmPin,0);
    }
    if(xTaskCreate(controlTask,"fan-lease",3072,nullptr,2,nullptr)!=pdPASS)abort();
    analogReadResolution(12);analogSetPinAttenuation(board::NtcPin,ADC_11db);
    Wire.begin(board::SdaPin,board::SclPin);Wire.setTimeOut(20);Wire.beginTransmission(board::LcdAddress);lcd=Wire.endTransmission()==0;
    if(lcd) {delay(50);lcdNibble(0x30);delay(5);lcdNibble(0x30);delay(1);lcdNibble(0x30);lcdNibble(0x20);lcdByte(0x28);lcdByte(0x0C);lcdByte(0x06);lcdByte(0x01);delay(2);}
    preferences.begin("victus-base",false);ssid=preferences.getString("ssid","");password=preferences.getString("password","");
    configured=preferences.getBool("valid",false)&&ssid.length()>0&&preferences.getBytesLength("pairing")==pairing.size();
    if(configured)preferences.getBytes("pairing",pairing.data(),pairing.size());
    WiFi.persistent(false);WiFi.mode(WIFI_STA);WiFi.setHostname(board::Hostname);WiFi.setAutoReconnect(false);
    Serial.println("Victus Base v1. Comandos USB: setup | status. Sin firmware fisico validado.");
}
void loop() {
    auto now=millis();serialInput();network(now);
    static uint32_t sensorAt=0,displayAt=0;
    if(static_cast<uint32_t>(now-sensorAt)>=1000) {sensorAt=now;readAmbient();}
    if(static_cast<uint32_t>(now-displayAt)>=1000) {displayAt=now;display(now);}
    delay(2);
}
