#ifndef VFC_PROBE_CONTRACT_H
#define VFC_PROBE_CONTRACT_H
#include <stdint.h>
#include <stddef.h>
#define VFC_PROBE_VERSION 1u
#define VFC_SELECTOR_COUNT 6u
/* Deliberately off until deployment, identity and security gates are reviewed. */
#define VFC_FIELD_PROBES_ENABLED 0
#define VFC_IOCTL_READ 0x00226000u /* UNKNOWN, function 0x800, BUFFERED, READ */
typedef struct { uint32_t Version, Selector; } VFC_READ_INPUT;
typedef struct {
    uint32_t Version, Selector, NativeStatus, Valid, Value, NativeBytes;
    uint64_t DurationTicks, Frequency;
    uint32_t CapturedBytes;
    unsigned char NativeData[20];
} VFC_READ_OUTPUT;
static const char VfcNames[VFC_SELECTOR_COUNT][5] = {
    "_STA", "SRP1", "SRP2", "SFAN", "FFFF", "FFFS"
};
static uint32_t VfcU32(const unsigned char *p) {
    return (uint32_t)p[0] | ((uint32_t)p[1]<<8) |
        ((uint32_t)p[2]<<16) | ((uint32_t)p[3]<<24);
}
/* Strict v1 ULONG result only; no packages, coercion or zero fallback. */
static int VfcParseInteger(const unsigned char *p, size_t bytes,
    uint32_t signature, uint32_t maximum, uint32_t *value) {
    if (!p || !value || bytes != 20 || VfcU32(p)!=signature ||
        VfcU32(p+4)!=20 || VfcU32(p+8)!=1 || p[12]!=0 || p[13]!=0 ||
        p[14]!=4 || p[15]!=0 || VfcU32(p+16)>maximum) return 0;
    *value=VfcU32(p+16);
    return 1;
}
#endif
