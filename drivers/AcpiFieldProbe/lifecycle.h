#ifndef VFC_PROBE_LIFECYCLE_H
#define VFC_PROBE_LIFECYCLE_H
#include <stdint.h>
#include <stddef.h>
/* Shared by the kernel callbacks and a host fault-injection fixture.
 * This tests ownership/sequencing, not WDF scheduling or ACPI cancellation. */
typedef struct {
    void *Target;
    uint32_t Used;
    int ControlPassed, Faulted, Qualified;
} VFC_PROBE_STATE;
typedef struct {
    int (*Identity)(void *device);
    int (*Create)(void *device, void **target);
    int (*Open)(void *device, void *target);
    void (*Close)(void *target);
    void (*Delete)(void *target);
} VFC_PROBE_OPS;
static void VfcProbePrepare(VFC_PROBE_STATE *state, void *device,
    const VFC_PROBE_OPS *ops) {
    void *target=NULL;
    if(!state->Qualified || state->Faulted)return;
    /* Unexpected duplicate prepare cannot overwrite/leak a live target. */
    if(state->Target){state->Faulted=1;return;}
    if(!ops->Identity(device)){state->Qualified=0;state->Faulted=1;return;}
    if(!ops->Create(device,&target)){state->Faulted=1;return;}
    if(!ops->Open(device,target)){
        ops->Delete(target);state->Faulted=1;return;
    }
    state->Target=target;
}
static void VfcProbeRelease(VFC_PROBE_STATE *state,const VFC_PROBE_OPS *ops) {
    void *target=state->Target;
    state->Target=NULL;
    if(target){ops->Close(target);ops->Delete(target);}
    /* Budget and fault latch survive release/prepare. */
}
#endif
