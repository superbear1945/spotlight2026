#ifndef FLEET_IDLE_RIG_INCLUDED
#define FLEET_IDLE_RIG_INCLUDED
#define RIG_TAU 6.28318530718

float2 Rotate2(float2 v,float a)
{ float s=sin(a),c=cos(a); return float2(c*v.x-s*v.y,s*v.x+c*v.y); }
float2 BladePlane(float2 v) { return float2(v.x+.22*v.y,.58*v.y); }
float2 UnBladePlane(float2 v) { float y=v.y/.58; return float2(v.x-.22*y,y); }

float2 LeverPoint(float2 p,float angle)
{
    float2 pivot=float2(1156.0,605.0)+_SourceOffset.xy;
    return pivot+BladePlane(Rotate2(UnBladePlane(p-pivot),angle));
}

float2 InverseMechanism(float2 p,float motion,float phase)
{
    float stroke=(.5-.5*cos(phase))*.135*_MechanicalAmount;
    if(motion>4.5&&motion<6.5)
        return LeverPoint(p,motion<5.5?-stroke:stroke);
    if(motion>.5&&motion<4.5)
    {
        bool upper=(motion<1.5)||(motion>2.5&&motion<3.5);
        float2 b=(upper?float2(927.0,502.0):float2(808.0,617.0))+_SourceOffset.xy;
        float2 a=(upper?float2(1079.0,555.0):float2(985.0,675.0))+_SourceOffset.xy;
        float2 target=LeverPoint(a,upper?-stroke:stroke);
        float2 oldDir=normalize(a-b),newDir=normalize(target-b);
        float oldAngle=atan2(oldDir.y,oldDir.x),newAngle=atan2(newDir.y,newDir.x);
        if(motion<2.5) return b+Rotate2(p-b,oldAngle-newAngle);
        float barrel=upper?118.0:130.0;
        float2 oldExit=b+oldDir*barrel,newExit=b+newDir*barrel;
        float oldLength=length(a-b)-barrel,newLength=max(12.0,length(target-b)-barrel);
        float2 q=p-newExit;
        float along=dot(q,newDir)*oldLength/newLength;
        float across=dot(q,float2(-newDir.y,newDir.x));
        return oldExit+oldDir*along+float2(-oldDir.y,oldDir.x)*across;
    }
    if(motion>9.5&&motion<13.5)
    {
        float index=motion-10.0;
        float2 pivot=motion<10.5?float2(819,430):motion<11.5?float2(876,440):motion<12.5?float2(826,876):float2(909,869);
        pivot+=_SourceOffset.xy;
        float a=sin(phase+index*.8)*.018*_MechanicalAmount;
        return pivot+Rotate2(p-pivot,-a);
    }
    if(motion>19.5)
    {
        float index=motion-20.0;
        float amount=(.5-.5*cos(phase*2.0+index*.8))*9.0*_MechanicalAmount;
        float2 direction=index<.5?float2(.94,.342):float2(.923,.385);
        return p-normalize(direction)*amount;
    }
    return p;
}

// A true 3D conical screw: a tapered shaft plus a helicoidal cutting fin.
// Rotation changes which side of each fin faces the camera, rather than rotating a flat picture.
float DrillDistance(float3 p,float angle)
{
    float h=_Drill.w;
    float normalized=clamp(p.y/h,0.0,1.0);
    float radius=_Drill.z*pow(max(1.0-normalized,0.0),.78);
    float radial=length(p.xz);
    float pitch=h/3.45;
    float helical=abs(frac(p.y/pitch-atan2(p.z,p.x)/RIG_TAU-angle/RIG_TAU+.5)-.5)*pitch;
    float fin=max(radial-radius,helical-4.2);
    float core=radial-radius*.61;
    float cone=max(min(core,fin),max(-p.y,p.y-h));
    float shaft=max(radial-28.0,max(-p.y-58.0,p.y));
    return min(cone,shaft);
}

float4 RotatingDrill(float2 p,float phase)
{
    if(_UnitKind<2.5||_Drill.z<1.0) return float4(0,0,0,0);
    float stroke=(.5-.5*cos(phase*2.0))*24.0*_MechanicalAmount;
    float2 screen=p-_Drill.xy-float2(0,stroke);
    if(abs(screen.x)>_Drill.z+6.0||screen.y< -66.0||screen.y>_Drill.w+36.0) return float4(0,0,0,0);
    const float cp=.9427547,sp=.3334871;
    float3 view=float3(0,sp,cp);
    float3 origin=float3(screen.x,screen.y*cp,-screen.y*sp)-view*180.0;
    float distanceAlong=0.0;float3 hit=float3(0,0,0);bool found=false;
    float angle=phase*2.0;
    for(int step=0;step<112;step++)
    {
        hit=origin+view*distanceAlong;
        float sd=DrillDistance(hit,angle);
        if(sd<.65){found=true;break;}
        distanceAlong+=max(.40,sd*.55);
        if(distanceAlong>410.0)break;
    }
    if(!found) return float4(0,0,0,0);
    float e=.5;
    float3 n=normalize(float3(
        DrillDistance(hit+float3(e,0,0),angle)-DrillDistance(hit-float3(e,0,0),angle),
        DrillDistance(hit+float3(0,e,0),angle)-DrillDistance(hit-float3(0,e,0),angle),
        DrillDistance(hit+float3(0,0,e),angle)-DrillDistance(hit-float3(0,0,e),angle)));
    float3 light=normalize(float3(-.45,-.70,-.55));
    float lit=clamp(dot(n,light)*.5+.5,0.0,1.0);
    lit=floor(lit*3.0+.5)/3.0;
    float3 material=n.y<-.28?float3(.69,.66,.58):float3(.26,.29,.34);
    if(n.y>.45) material=float3(.13,.15,.19);
    if(hit.y<0.0) material=float3(.39,.42,.47);
    float3 color=material*(.65+.35*lit);
    float localRadius=_Drill.z*pow(max(1.0-clamp(hit.y/_Drill.w,0.0,1.0),0.0),.78);
    float radial=length(hit.xz);
    if(hit.y>=0.0&&radial>localRadius-2.7)color=float3(.055,.063,.078);
    if(hit.y>=0.0&&radial<localRadius*.67)
        color+=float3(.10,.105,.11)*pow(clamp(dot(n,normalize(float3(-.6,-.15,-.8))),0.0,1.0),14.0);
    float facing=abs(dot(n,view));
    if(facing<.20)color=float3(.045,.055,.072);
    return float4(color,1.0);
}

void ReadRig(float2 p,float phase,out float4 art,out float4 effectMask)
{
    float3 premul=float3(0,0,0);
    float alpha=0.0;
    // Four subpixel rays give the small 3D screw the same clean edge as the antialiased source art.
    float2 pixel=float2(length(float2(ddx(p.x),ddy(p.x))),length(float2(ddx(p.y),ddy(p.y))))*.25;
    if(_UnitKind>2.5)
    {
        for(int aa=0;aa<4;aa++)
        {
            float2 signOffset=float2((aa==0||aa==2)?-1.0:1.0,aa<2?-1.0:1.0);
            float4 drill=RotatingDrill(p+pixel*signOffset,phase);
            premul+=drill.rgb*drill.a*.25;alpha+=drill.a*.25;
        }
    }
    effectMask=float4(0,0,0,0);
    for(int i=0;i<12;i++)
    {
        if(float(i)>=_PartCount)break;
        float2 q=InverseMechanism(p,_PartInfo[i].x,phase);
        float4 source=_PartSource[i],tile=_PartAtlas[i];
        float2 local=q-source.xy;
        if(local.x<-.5||local.y<-.5||local.x>source.z-.5||local.y>source.w-.5)continue;
        float2 atlasPoint=tile.xy+local;
        float4 color=ReadRigAtlas(atlasPoint);
        if(color.a<.001)continue;
        float4 m=ReadRigMaskAtlas(atlasPoint);
        premul=color.rgb*color.a+premul*(1.0-color.a);
        alpha=color.a+alpha*(1.0-color.a);
        effectMask=lerp(effectMask,m,color.a);
    }
    art=float4(alpha>.0001?premul/alpha:float3(0,0,0),alpha);
}
#endif
