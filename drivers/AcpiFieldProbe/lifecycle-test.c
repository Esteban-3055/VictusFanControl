#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include "lifecycle.h"
typedef struct {
    int IdentityOk,CreateOk,OpenOk;
    int Identities,Creates,Opens,Closes,Deletes,Alive,Opened;
    char Trace[32];size_t TraceBytes;
} FIXTURE;
static FIXTURE f;
static unsigned tests;
#define CHECK(x) do { if(!(x)){fprintf(stderr,"Failed line %d: %s\n",__LINE__,#x);exit(1);} } while(0)
static void Trace(char step){CHECK(f.TraceBytes+1<sizeof(f.Trace));f.Trace[f.TraceBytes++]=step;}
static int Identity(void *device){CHECK(device==&f);++f.Identities;Trace('I');return f.IdentityOk;}
static int Create(void *device,void **target){
    CHECK(device==&f && !f.Alive);++f.Creates;Trace('C');
    if(!f.CreateOk)return 0;
    f.Alive=1;*target=&f;return 1;
}
static int Open(void *device,void *target){
    CHECK(device==&f && target==&f && f.Alive && !f.Opened);
    ++f.Opens;Trace('O');if(!f.OpenOk)return 0;f.Opened=1;return 1;
}
static void Close(void *target){CHECK(target==&f && f.Alive && f.Opened);++f.Closes;Trace('X');f.Opened=0;}
static void Delete(void *target){CHECK(target==&f && f.Alive && !f.Opened);++f.Deletes;Trace('D');f.Alive=0;}
static const VFC_PROBE_OPS ops={Identity,Create,Open,Close,Delete};
static VFC_PROBE_STATE Reset(void){
    VFC_PROBE_STATE state;
    memset(&f,0,sizeof(f));memset(&state,0,sizeof(state));
    f.IdentityOk=f.CreateOk=f.OpenOk=1;state.Qualified=1;return state;
}
static void ExpectTrace(const char *trace){CHECK(strcmp(f.Trace,trace)==0);}
int main(void){
    VFC_PROBE_STATE state;int stage;
    state=Reset();state.Qualified=0;VfcProbePrepare(&state,&f,&ops);
    CHECK(!state.Target && !state.Faulted);ExpectTrace("");++tests;
    state=Reset();state.Faulted=1;VfcProbePrepare(&state,&f,&ops);
    CHECK(!state.Target);ExpectTrace("");++tests;
    state=Reset();f.IdentityOk=0;VfcProbePrepare(&state,&f,&ops);
    CHECK(!state.Qualified && state.Faulted && !state.Target);ExpectTrace("I");++tests;
    state=Reset();f.CreateOk=0;VfcProbePrepare(&state,&f,&ops);
    CHECK(state.Faulted && !state.Target && !f.Alive);ExpectTrace("IC");++tests;
    state=Reset();f.OpenOk=0;VfcProbePrepare(&state,&f,&ops);
    CHECK(state.Faulted && !state.Target && !f.Alive && f.Deletes==1 && !f.Closes);
    ExpectTrace("ICOD");++tests;
    for(stage=0;stage<3;++stage){
        state=Reset();
        if(stage==0)f.IdentityOk=0;
        if(stage==1)f.CreateOk=0;
        if(stage==2)f.OpenOk=0;
        state.Used=1;state.ControlPassed=1;
        VfcProbePrepare(&state,&f,&ops);
        {
            FIXTURE before=f;
            VfcProbePrepare(&state,&f,&ops);VfcProbeRelease(&state,&ops);
            VfcProbeRelease(&state,&ops);VfcProbePrepare(&state,&f,&ops);
            CHECK(memcmp(&before,&f,sizeof(f))==0);
        }
        CHECK(state.Faulted && state.Used==1 && state.ControlPassed && !state.Target);
        ++tests;
    }
    state=Reset();VfcProbePrepare(&state,&f,&ops);
    CHECK(state.Target==&f && !state.Faulted && f.Alive && f.Opened);
    ExpectTrace("ICO");++tests;
    state.Used=5;state.ControlPassed=1;
    VfcProbeRelease(&state,&ops);VfcProbeRelease(&state,&ops);
    CHECK(!state.Target && !f.Alive && state.Used==5 && state.ControlPassed);
    CHECK(f.Closes==1 && f.Deletes==1);ExpectTrace("ICOXD");++tests;
    VfcProbePrepare(&state,&f,&ops);
    CHECK(state.Target==&f && state.Used==5 && state.ControlPassed && !state.Faulted);
    CHECK(f.Creates==2 && f.Opens==2);++tests;
    state.Faulted=1;VfcProbeRelease(&state,&ops);VfcProbePrepare(&state,&f,&ops);
    CHECK(!state.Target && state.Faulted && state.Used==5 && !f.Alive);
    CHECK(f.Creates==2 && f.Closes==2 && f.Deletes==2);++tests;
    state=Reset();VfcProbePrepare(&state,&f,&ops);f.IdentityOk=0;
    VfcProbeRelease(&state,&ops);VfcProbePrepare(&state,&f,&ops);
    CHECK(!state.Qualified && state.Faulted && !state.Target && f.Creates==1 && !f.Alive);
    ExpectTrace("ICOXDI");++tests;
    state=Reset();VfcProbePrepare(&state,&f,&ops);VfcProbePrepare(&state,&f,&ops);
    CHECK(state.Faulted && state.Target==&f && f.Creates==1 && f.Alive);
    VfcProbeRelease(&state,&ops);
    CHECK(!state.Target && !f.Alive && f.Deletes==1);ExpectTrace("ICOXD");++tests;
    printf("ACPI probe lifecycle: %u host fault-injection cases passed; no driver loaded or ACPI evaluated.\n",tests);
    return 0;
}
