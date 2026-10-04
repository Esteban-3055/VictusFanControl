#include <stdio.h>
#include <string.h>
#include "identity.h"
static void Put32(unsigned char*p,uint32_t v){p[0]=(unsigned char)v;p[1]=(unsigned char)(v>>8);p[2]=(unsigned char)(v>>16);p[3]=(unsigned char)(v>>24);}
static size_t Record(unsigned char*p,unsigned type,unsigned length,const char*strings,size_t n){
    memset(p,0,length);p[0]=(unsigned char)type;p[1]=(unsigned char)length;
    memcpy(p+length,strings,n);return length+n;
}
int main(void){
    unsigned char good[512]={0},bad[512];size_t n=8,a,b,c,size,i;
    const char bios[]="F.18\0";
    const char system[]="HP\0Victus by HP Gaming Laptop 15-fa1xxx\0" "9D0R1LA#AKH\0";
    const char board[]="HP\0" "8C40\0" "63.43\0";
    a=n;n+=Record(good+n,0,0x18,bios,sizeof(bios));good[a+5]=1;
    b=n;n+=Record(good+n,1,0x1b,system,sizeof(system));good[b+4]=1;good[b+5]=2;good[b+0x19]=3;
    c=n;n+=Record(good+n,2,0x0f,board,sizeof(board));good[c+4]=1;good[c+5]=2;good[c+6]=3;
    n+=Record(good+n,127,4,"\0",2);good[1]=3;good[2]=5;Put32(good+4,(uint32_t)(n-8));
    if(!VfcMatchRawSmbios(good,n))return 1;
    for(size=0;size<n;++size){
        memcpy(bad,good,n);if(size>=8)Put32(bad+4,(uint32_t)(size-8));
        if(VfcMatchRawSmbios(bad,size))return 2;
    }
    for(i=0;i<3;++i){size_t offset=i==0?a:i==1?b:c;
        memcpy(bad,good,n);bad[offset+1]=3;if(VfcMatchRawSmbios(bad,n))return 3;
        memcpy(bad,good,n);bad[offset]=(unsigned char)(i+40);if(VfcMatchRawSmbios(bad,n))return 4;
    }
    memcpy(bad,good,n);bad[a+5]=0;if(VfcMatchRawSmbios(bad,n))return 5;
    memcpy(bad,good,n);bad[b+0x19]=250;if(VfcMatchRawSmbios(bad,n))return 6;
    memcpy(bad,good,n);bad[c+0x0f+3]='9';if(VfcMatchRawSmbios(bad,n))return 7;
    memcpy(bad,good,n);bad[a+0x18+3]='9';if(VfcMatchRawSmbios(bad,n))return 8;
    memcpy(bad,good,n);bad[n-1]=1;if(VfcMatchRawSmbios(bad,n))return 9;
    memcpy(bad,good,n);bad[c]=1;if(VfcMatchRawSmbios(bad,n))return 10;
    memcpy(bad,good,n);Put32(bad+4,(uint32_t)n);if(VfcMatchRawSmbios(bad,n))return 11;
    memcpy(bad,good,n-6);memcpy(bad+n-6,good+a,b-a);memcpy(bad+n-6+b-a,good+n-6,6);
    Put32(bad+4,(uint32_t)(n+b-a-8));if(VfcMatchRawSmbios(bad,n+b-a))return 13;
    memcpy(bad,good,n);bad[b+0x1b+sizeof(system)-3]='X';if(VfcMatchRawSmbios(bad,n))return 14;
    /* Deterministic arbitrary/truncated inputs must not overrun the parser. */
    for(i=0;i<512;++i)bad[i]=(unsigned char)(i*73+19);
    for(size=0;size<512;++size)if(VfcMatchRawSmbios(bad,size))return 12;
    puts("SMBIOS identity fixtures passed: exact profile, all truncations, malformed strings, missing/duplicate records and foreign board/BIOS.");return 0;
}
