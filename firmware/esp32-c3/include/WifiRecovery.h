#pragma once
#include <cstdint>

namespace victus {
// Supplement the driver's AutoReconnect only after a full minute without any
// station event. Never restart a progressing association every few seconds.
class WifiRecovery {
    bool waiting_ = false;
    uint32_t attemptedAt_ = 0, interval_ = 60000, attempts_ = 0;
public:
    void reset() { waiting_ = false; interval_ = 60000; attempts_ = 0; }
    uint32_t attempts() const { return attempts_; }
    bool retry(uint32_t now, bool enabled, bool connected, uint32_t lastEventAt) {
        if (!enabled || connected) { reset(); return false; }
        if (!waiting_) { waiting_ = true; attemptedAt_ = now; return false; }
        if (static_cast<uint32_t>(now - lastEventAt) < 60000 ||
            static_cast<uint32_t>(now - attemptedAt_) < interval_) return false;
        attemptedAt_ = now;
        interval_ = interval_ < 150000 ? interval_ * 2 : 300000;
        ++attempts_;
        return true;
    }
};
}
