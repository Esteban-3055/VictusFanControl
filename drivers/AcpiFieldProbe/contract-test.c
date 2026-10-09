#include <stdio.h>
#include <string.h>
#include "contract.h"
static void Put(unsigned char *p,uint32_t v){p[0]=(unsigned char)v;p[1]=(unsigned char)(v>>8);p[2]=(unsigned char)(v>>16);p[3]=(unsigned char)(v>>24);}
int main(void){
    unsigned char good[20]={0},bad[20];uint32_t value=777;size_t n;
    const uint32_t sig=0x426f6541;
    if(sizeof(VFC_READ_INPUT)!=8 || sizeof(VFC_READ_OUTPUT)!=64 || VFC_FIELD_PROBES_ENABLED) return 1;
    Put(good,sig);Put(good+4,20);Put(good+8,1);good[14]=4;Put(good+16,255);
    if(!VfcParseInteger(good,20,sig,255,&value) || value!=255) return 2;
    for(n=0;n<20;++n) if(VfcParseInteger(good,n,sig,255,&value)) return 3;
    if(VfcParseInteger(good,21,sig,255,&value) || VfcParseInteger(good,20,sig,1,&value))return 4;
    for(n=0;n<16;++n){memcpy(bad,good,20);bad[n]^=1;value=777;
        if(VfcParseInteger(bad,20,sig,255,&value) || value!=777)return 5;}
    Put(good+16,0);value=777;
    if(!VfcParseInteger(good,20,sig,1,&value) || value) return 6;
    if(strcmp(VfcNames[1],"SRP1") || strcmp(VfcNames[3],"SFAN"))return 7;
    puts("ACPI integer contract passed: truncation, signature, size, count, type, range and zero handling.");return 0;
}
