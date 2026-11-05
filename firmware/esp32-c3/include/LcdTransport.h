#pragma once
#include <cstddef>
#include <cstdint>

namespace victus {
// Bus supplies write(bytes, size) -> I2C status and wait(microseconds).
// Abort at the first failed transaction: a partial E pulse/nibble must not
// be followed by more characters. Only a complete initialization restores ready.
template<class Bus> class LcdTransport {
    Bus& bus;
    bool active = false;
    bool send(const uint8_t* bytes, size_t count) {
        if (!active) return false;
        if (bus.write(bytes, count) != 0) active = false;
        return active;
    }
    bool nibble(uint8_t value) {
        const uint8_t bytes[]{static_cast<uint8_t>(value|8), static_cast<uint8_t>(value|12),
                              static_cast<uint8_t>(value|8)};
        return send(bytes, sizeof(bytes));
    }
    bool byte(uint8_t value, bool text = false) {
        if (!nibble((value&0xf0)|(text?1:0)) || !nibble((value<<4)|(text?1:0))) return false;
        bus.wait(50); // HD44780 instruction execution; independent of I2C speed.
        return true;
    }
public:
    explicit LcdTransport(Bus& io):bus(io) {}
    bool ready() const { return active; }
    bool initialize() {
        active = true;
        const uint8_t idle = 8; // Force E/RW/RS low after a failed partial write.
        if (!send(&idle, 1)) return false;
        bus.wait(60000);
        if (!nibble(0x30)) return false;
        bus.wait(5000);
        if (!nibble(0x30)) return false;
        bus.wait(150);
        if (!nibble(0x30)) return false;
        bus.wait(150);
        if (!nibble(0x20)) return false;
        bus.wait(150);
        if (!byte(0x28) || !byte(0x0c) || !byte(0x06) || !byte(0x01)) return false;
        bus.wait(2000);
        return true;
    }
    bool row(uint8_t address, const char (&text)[17]) {
        if (!byte(address)) return false;
        for (int i=0; i<16; ++i) if (!byte(text[i], true)) return false;
        return true;
    }
};
} // namespace victus
