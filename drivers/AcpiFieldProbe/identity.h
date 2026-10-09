#ifndef VFC_PROBE_IDENTITY_H
#define VFC_PROBE_IDENTITY_H
#include <stdint.h>
#include <stddef.h>
/* No serials, UUIDs or user-provided profile. SMBIOS is identification, not attestation. */
static uint32_t VfcIdentityU32(const unsigned char *p){
    return (uint32_t)p[0]|((uint32_t)p[1]<<8)|((uint32_t)p[2]<<16)|((uint32_t)p[3]<<24);
}
static int VfcIdentityString(const unsigned char *p,size_t begin,size_t end,unsigned index,const char *expected){
    unsigned current=1;size_t i=begin,j;
    if(!index)return 0;
    while(i<end && p[i]){
        size_t start=i;
        while(i<end && p[i])++i;
        if(i==end)return 0;
        if(current==index){
            for(j=0;expected[j];++j)if(start+j>=i || p[start+j]!=(unsigned char)expected[j])return 0;
            return start+j==i;
        }
        ++i;++current;
    }
    return 0;
}
static int VfcMatchRawSmbios(const unsigned char *p,size_t bytes){
    size_t pos=8,end,next;unsigned seen=0;
    if(!p || bytes<8 || bytes>1024*1024 || VfcIdentityU32(p+4)!=bytes-8 ||
        p[1]<2 || p[1]>3 || (p[1]==2 && p[2]<4))return 0;
    while(pos<bytes){
        unsigned type,length,bit=0;
        if(bytes-pos<4)return 0;
        type=p[pos];length=p[pos+1];
        if(length<4 || length>bytes-pos)return 0;
        end=pos+length;
        while(end+1<bytes && (p[end] || p[end+1]))++end;
        if(end+1>=bytes)return 0;
        next=end+2;
        if(type==127)return length==4 && next==bytes && seen==7;
        if(type==0){
            bit=1;
            if(length<0x12 || !VfcIdentityString(p,pos+length,end+1,p[pos+5],"F.18"))return 0;
        }else if(type==1){
            bit=2;
            if(length<0x1b || !VfcIdentityString(p,pos+length,end+1,p[pos+4],"HP") ||
                !VfcIdentityString(p,pos+length,end+1,p[pos+5],"Victus by HP Gaming Laptop 15-fa1xxx") ||
                !VfcIdentityString(p,pos+length,end+1,p[pos+0x19],"9D0R1LA#AKH"))return 0;
        }else if(type==2){
            bit=4;
            if(length<8 || !VfcIdentityString(p,pos+length,end+1,p[pos+4],"HP") ||
                !VfcIdentityString(p,pos+length,end+1,p[pos+5],"8C40") ||
                !VfcIdentityString(p,pos+length,end+1,p[pos+6],"63.43"))return 0;
        }
        if(bit && (seen&bit))return 0;
        seen|=bit;pos=next;
    }
    return 0; /* Missing end-of-table is never accepted. */
}
#endif
