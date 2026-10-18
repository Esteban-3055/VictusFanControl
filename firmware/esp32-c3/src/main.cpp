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
#include "LcdPresentation.h"
#include "LcdTransport.h"
#include "NtcSensor.h"
#if __has_include("config.local.h")
#include "config.local.h"
#endif

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
bool wifiStarted=false;
uint32_t lastPowerCheck=0;
victus::AmbientSample ambient;
portMUX_TYPE ambientLock=portMUX_INITIALIZER_UNLOCKED;
std::atomic<int> applied{0};
std::atomic<bool> remote{false};
std::atomic<int> externalRequest{0};
portMUX_TYPE configLock=portMUX_INITIALIZER_UNLOCKED;
char appliedRevision[33]{};
victus::AmbientFilter ambientFilter;
// Last ADC read is owned by loop()/serialInput(); only the filtered
// ambient measurement crosses to the independent FreeRTOS control task.
struct NtcDiagnostic {
    unsigned raw = 0, millivolts = 0;
    double unfilteredC = NAN;
    uint32_t sampledAt = 0;
    const char* reason = "NO_SAMPLE";
} ntcDiagnostic;
uint32_t ntcValidSamples=0, ntcInvalidSamples=0, ntcLastInvalidAt=0;
const char* ntcLastInvalidReason="none";
struct Command { bool valid; int request; uint32_t at; double demand=-1; bool manual=false,hasConfig=false; victus::ExternalConfig config; };
QueueHandle_t commands;
String ssid,password;
std::string storedCurveRevision;
uint32_t lastCurveSave=0;
bool curveSavedThisBoot=false;
std::string serialLine,stagedSsid,stagedPassword;
int provision=0;
bool overflow=false;
double currentAmbient() {
    // Snapshot value+time together; take time after publication to avoid a
    // false expired sample if the control task was preempted by the ADC reader.
    portENTER_CRITICAL(&ambientLock);
    auto sample=ambient;
    uint32_t now=millis();
    portEXIT_CRITICAL(&ambientLock);
    return sample.current(now);
}

void controlTask(void*) {
    victus::ExternalController controller;
    for (;;) {
        Command command{};
        if (xQueueReceive(commands,&command,0)==pdTRUE) {
            if(command.hasConfig) controller.configure(command.config);
            controller.receive(command.valid,command.request,command.demand,command.manual,command.at);
        }
        int duty=controller.tick(millis(),currentAmbient());
        if (board::EnablePwm) {
#if ESP_ARDUINO_VERSION_MAJOR >= 3
            ledcWrite(board::PwmPin,board::LedcDutyFrom8Bit(victus::ExternalController::electrical(duty)));
#else
            ledcWrite(0,board::LedcDutyFrom8Bit(victus::ExternalController::electrical(duty)));
#endif
        }
        applied.store(duty); remote.store(controller.remote());externalRequest.store(controller.requested());
        portENTER_CRITICAL(&configLock);snprintf(appliedRevision,sizeof(appliedRevision),"%s",controller.revision());portEXIT_CRITICAL(&configLock);
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
    // Bounded readback, including while associating. Never poll driver power at loop frequency.
    if(wifiStarted&&static_cast<uint32_t>(now-lastPowerCheck)>=5000) {
        lastPowerCheck=now;
        if(WiFi.getTxPower()!=WIFI_POWER_8_5dBm) WiFi.setTxPower(WIFI_POWER_8_5dBm);
    }
    if (WiFi.status()!=WL_CONNECTED) {
        if (networkReady || listener>=0 || peer>=0) stopNetwork();
        if (!wifiStarted) {
            WiFi.setAutoReconnect(true);WiFi.setSleep(false);
            WiFi.begin(ssid.c_str(),password.c_str());
            wifiStarted=true;
            if(!WiFi.setTxPower(WIFI_POWER_8_5dBm)) Serial.println("No se pudo aplicar potencia WiFi 8.5 dBm.");
        }
        return;
    }
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
            char revision[33];portENTER_CRITICAL(&configLock);memcpy(revision,appliedRevision,33);portEXIT_CRITICAL(&configLock);
            session->appliedConfigRevision=revision;session->appliedDuty=board::EnablePwm?applied.load():-1;
            session->externalRequest=board::EnablePwm?externalRequest.load():-1;
            if (!session->accept(input,millis(),currentAmbient(),board::EnablePwm?static_cast<int>(lround(applied.load()*100.0/255)):-1,board::EnablePwm&&remote.load(),response)) {
                disconnectPeer();nextAccept=now+1000;Serial.println("Sesion rechazada o terminada.");return;
            }
            Command command{session->remoteRequest && board::EnablePwm,session->latest.request,millis()};
            command.demand=session->latest.demand;command.manual=session->latest.manual;
            command.hasConfig=session->hasConfiguration;command.config=session->configuration;
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

    // Preserve the existing fail-safe limits until the actual ADC values
    // have been measured on the ESP32-C3. Expose individual rejection reasons.
    ntcDiagnostic.raw=raw;
    ntcDiagnostic.millivolts=mv;
    ntcDiagnostic.unfilteredC=NAN;
    ntcDiagnostic.sampledAt=millis();
    auto result=victus::convertNtc(raw,mv);
    const char* reason=result.reason;
    double c=result.c;

    ntcDiagnostic.reason=reason;
    if(strcmp(reason,"OK")==0) {
        ntcDiagnostic.unfilteredC=c;
        ++ntcValidSamples;
    } else {
        ++ntcInvalidSamples;
        ntcLastInvalidAt=ntcDiagnostic.sampledAt;
        ntcLastInvalidReason=reason;
    }
    double filtered=ambientFilter.update(c);
    portENTER_CRITICAL(&ambientLock);
    ambient={filtered,ntcDiagnostic.sampledAt};
    portEXIT_CRITICAL(&ambientLock);
}
void printNtcDiagnostic() {
    uint32_t now=millis();
    Serial.printf("NTC GPIO%d raw=%u adc=%u mV motivo=%s",
                  board::NtcPin,ntcDiagnostic.raw,ntcDiagnostic.millivolts,ntcDiagnostic.reason);
    if(ntcDiagnostic.sampledAt==0) Serial.print(" muestra=pendiente");
    else Serial.printf(" antiguedad=%lu ms",
                       static_cast<unsigned long>(static_cast<uint32_t>(now-ntcDiagnostic.sampledAt)));
    if(std::isfinite(ntcDiagnostic.unfilteredC)) {
        Serial.print(" temperatura=");
        Serial.print(ntcDiagnostic.unfilteredC,1);
        Serial.print(" C");
    }
    double current=currentAmbient();
    if(std::isfinite(current)) {
        Serial.print(" filtrada=");
        Serial.print(current,1);
        Serial.println(" C");
    } else Serial.print(" filtrada=INVALIDA (NTC Fault)");
    Serial.printf(" lecturas_ok=%lu lecturas_fallo=%lu ultima_falla=%s",
                  static_cast<unsigned long>(ntcValidSamples),
                  static_cast<unsigned long>(ntcInvalidSamples),ntcLastInvalidReason);
    if(ntcLastInvalidAt!=0)
        Serial.printf(" hace=%lu ms",
                      static_cast<unsigned long>(static_cast<uint32_t>(now-ntcLastInvalidAt)));
    Serial.println();
}
// LCD1602 with PCF8574 (P0=RS,P1=RW,P2=E,P3=backlight,P4..7=data).
// Display errors are never fed back into fan control. An I2C write failure
// can leave OLD characters on the physical display even when the NTC recovers.
uint8_t lcdAddress=board::LcdAddress;
uint8_t lcdLastI2cError=0;
uint32_t lcdErrorCount=0, lcdLastErrorAt=0, lcdLastRefreshAt=0;
uint32_t lcdCycleStartedAt=0;

struct LcdBus {
    uint8_t write(const uint8_t* data,size_t count) {
        Wire.beginTransmission(lcdAddress);Wire.write(data,count);
        uint8_t error=Wire.endTransmission();
        if(error) {lcdLastI2cError=error;lcdLastErrorAt=millis();++lcdErrorCount;}
        return error;
    }
    void wait(uint32_t us) { if(us>=1000) delay(us/1000);else delayMicroseconds(us); }
} lcdBus;
victus::LcdTransport<LcdBus> lcdDriver(lcdBus);
void probeLcd() {
    for(uint8_t address=0x20;address<=0x3f;++address) {
        if(address>0x27&&address<0x38)continue;
        Wire.beginTransmission(address);
        if(Wire.endTransmission()!=0)continue;
        lcdAddress=address;
        if(lcdDriver.initialize()) {
            Serial.printf("LCD detectado: 0x%02X (I2C activo)\n",address);
            return;
        }
    }
}
void printLcdDiagnostic() {
    uint32_t now=millis();
    Serial.printf("LCD activo=%s direccion=0x%02X errores_I2C=%lu ultimo_error=%u",
                  lcdDriver.ready()?"si":"no",lcdAddress,static_cast<unsigned long>(lcdErrorCount),lcdLastI2cError);
    if(lcdLastRefreshAt)
        Serial.printf(" ultima_pantalla_hace=%lu ms",
                      static_cast<unsigned long>(static_cast<uint32_t>(now-lcdLastRefreshAt)));
    else Serial.print(" pantalla_nunca_actualizada");
    if(lcdLastErrorAt)
        Serial.printf(" ultimo_error_hace=%lu ms",
                      static_cast<unsigned long>(static_cast<uint32_t>(now-lcdLastErrorAt)));
    Serial.println();
}
void display(uint32_t now) {
    if(!lcdDriver.ready()) return;
    // All 4 pages fit 16x2. This is presentation only: nothing here can
    // renew a remote request or change the fan controller's fail-safe.
    victus::LcdReadings view;
    view.ambientC=currentAmbient();
    view.pwmEnabled=board::EnablePwm;
    view.pwmDuty=board::EnablePwm?applied.load():-1;
    view.wifi=WiFi.status()==WL_CONNECTED;
    view.pcAuthenticated=session&&session->authenticated();
    if(view.pcAuthenticated&&session->latest.linkFresh(now)) {
        const auto& m=session->latest;
        view.telemetryFresh=true;
        view.cpuPct=m.fresh(m.cpu,m.usageAge,now);
        view.gpuPct=m.fresh(m.gpu,m.usageAge,now);
        view.ramPct=m.fresh(m.ram,m.ramAge,now);
        view.demandPct=m.fresh(m.demand,m.demandAge,now);
        view.cpuTempC=m.fresh(m.cpuTempC,m.cpuTempAge,now);
        view.gpuTempC=m.fresh(m.gpuTempC,m.gpuTempAge,now);
    }
    uint32_t elapsed=victus::lcdElapsed(now,lcdCycleStartedAt);
    victus::LcdPage page=victus::lcdPage(elapsed);
    char first[17]{},second[17]{};
    victus::renderLcd(page,view,first,second);
    lcdDriver.row(0x80,first);
    lcdDriver.row(0xC0,second);
    if(lcdDriver.ready()) lcdLastRefreshAt=millis();
    else Serial.printf("LCD: fallo I2C codigo=%u; imagen anterior puede quedar congelada.\n",lcdLastI2cError);
}
void wipe(std::string& s) { volatile char* p=s.empty()?nullptr:&s[0]; for(size_t i=0;i<s.size();++i)p[i]=0;s.clear(); }
void provisioningLine(std::string& line) {
    if (!provision) {
        if (line=="setup") { stopNetwork(); WiFi.setAutoReconnect(false);WiFi.disconnect();wifiStarted=false; provision=1;Serial.println("SSID (sin eco):"); }
        else if (line=="status") { Serial.printf("Configurado=%s PWM=%s Modo=%s\n",configured?"si":"no",board::EnablePwm?"activo":"desactivado",remote.load()?"Remote":"Local"); }
        else if (line=="ntc") printNtcDiagnostic();
        else if (line=="lcd") printLcdDiagnostic();
        else if (line=="erase CONFIRM") { stopNetwork();WiFi.setAutoReconnect(false);WiFi.disconnect(true,true);wifiStarted=false;preferences.clear();configured=false;pairing.fill(0);ssid="";password="";Serial.println("Configuracion borrada."); }
        else Serial.println("Comandos USB: setup | status | ntc | lcd | erase CONFIRM");
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
        pinMode(board::PwmPin,OUTPUT);digitalWrite(board::PwmPin,board::PwmActiveLow?HIGH:LOW);
#if ESP_ARDUINO_VERSION_MAJOR >= 3
        if(!ledcAttach(board::PwmPin,board::PwmFrequencyHz,board::PwmResolutionBits))abort();
        ledcWrite(board::PwmPin,board::LedcDutyFrom8Bit(victus::ExternalController::electrical(0)));
#else
        if(ledcSetup(0,board::PwmFrequencyHz,board::PwmResolutionBits)==0)abort();
        ledcWrite(0,board::LedcDutyFrom8Bit(victus::ExternalController::electrical(0)));ledcAttachPin(board::PwmPin,0);
#endif
        // GPIO3 is active-low via 2N7000 -> BC337. Initial full electrical
        // duty requests logical zero; Local then ramps towards 130.
        Serial.printf("PWM LEDC: %lu Hz, %d bits, GPIO%d; 8-bit external curve scaled to %lu.\n",
                      static_cast<unsigned long>(board::PwmFrequencyHz),
                      board::PwmResolutionBits,board::PwmPin,
                      static_cast<unsigned long>(board::LedcMaximumDuty));
    }
    preferences.begin("victus-base",false);
    victus::ExternalConfig restored;
    if(preferences.getBytesLength("curve-v1")==sizeof(restored)&&preferences.getBytes("curve-v1",&restored,sizeof(restored))==sizeof(restored)&&restored.valid()) {
        Command boot{false,0,millis()};boot.hasConfig=true;boot.config=restored;xQueueOverwrite(commands,&boot);storedCurveRevision=restored.revision;
    }
    if(xTaskCreate(controlTask,"fan-lease",4096,nullptr,2,nullptr)!=pdPASS)abort();
    analogReadResolution(12);analogSetPinAttenuation(board::NtcPin,ADC_11db);
    Wire.begin(board::SdaPin,board::SclPin);Wire.setClock(100000);Wire.setTimeOut(20);probeLcd();
    ssid=preferences.getString("ssid","");password=preferences.getString("password","");
    configured=preferences.getBool("valid",false)&&ssid.length()>0&&preferences.getBytesLength("pairing")==pairing.size();
    if(configured)preferences.getBytes("pairing",pairing.data(),pairing.size());
#if defined(WIFI_SSID) && defined(WIFI_PASSWORD) && defined(PAIRING_KEY_B64)
    // Optional private Work 0.4 credentials. Existing USB/NVS pairing takes precedence.
    if(!configured&&victus::decodeKey(PAIRING_KEY_B64,pairing)&&std::string(PAIRING_KEY_B64)!="AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=") {
        ssid=WIFI_SSID;password=WIFI_PASSWORD;configured=ssid.length()>0&&ssid.length()<=32;
    }
#endif
    WiFi.persistent(false);WiFi.mode(WIFI_STA);WiFi.setHostname(board::Hostname);WiFi.setAutoReconnect(true);WiFi.setSleep(false);
    Serial.println("Victus Base v1. Comandos USB: setup | status | ntc | lcd. Sin firmware fisico validado.");
    Serial.println("Politica externa: NTC solo lectura; sin orden valida, rampa a PWM 130/255.");
    lcdCycleStartedAt=millis();
    display(lcdCycleStartedAt);
}
void loop() {
    serialInput();network(millis());
    auto now=millis();
    static uint32_t sensorAt=0,displayAt=0,probeAt=0;
    if(static_cast<uint32_t>(now-sensorAt)>=1000) {sensorAt=now;readAmbient();}
    // Read the NTC before any LCD power-up wait. Re-read time after IO.
    if(!lcdDriver.ready()&&static_cast<uint32_t>(now-probeAt)>=5000) {
        probeAt=now;probeLcd();
        if(lcdDriver.ready()) display(millis());
    }
    now=millis();
    victus::lcdElapsed(now,lcdCycleStartedAt); // Also roll the clock while disconnected.
    if(static_cast<uint32_t>(now-displayAt)>=250) {displayAt=now;display(now);}
    if(session&&session->hasConfiguration&&storedCurveRevision!=session->configuration.revision&&
        (!curveSavedThisBoot||static_cast<uint32_t>(now-lastCurveSave)>=30000)) {
        curveSavedThisBoot=true;lastCurveSave=now;
        if(preferences.putBytes("curve-v1",&session->configuration,sizeof(victus::ExternalConfig))==sizeof(victus::ExternalConfig))storedCurveRevision=session->configuration.revision;
        else Serial.println("No se pudo persistir la curva externa; se conserva en RAM.");
    }
    delay(2);
}
