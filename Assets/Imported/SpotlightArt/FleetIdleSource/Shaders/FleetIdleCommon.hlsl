#ifndef FLEET_IDLE_COMMON_INCLUDED
#define FLEET_IDLE_COMMON_INCLUDED

// Coordinates are source pixels with the origin at the top left.
// ReadArt and ReadMask are supplied by the Unity / offline GLSL wrappers.
#define FLEET_TAU 6.28318530718

float Bell(float x) { return exp(-x*x); }

float3 EngineFlow(float2 p,float4 engine,float4 direction,float phase)
{
    if(engine.z<1.0) return float3(0.0,0.0,0.0);
    float2 dir=normalize(direction.xy);
    float2 d=p-engine.xy;
    float along=dot(d,dir);
    float across=dot(d,float2(-dir.y,dir.x));
    float ph=phase+direction.z;
    float lengthNow=engine.z*(0.94+0.045*sin(ph*7.0)+0.025*sin(ph*11.0));
    float s=along/lengthNow;
    if(s<-.20||s>1.36||abs(across)>engine.w*2.3) return float3(0.0,0.0,0.0);
    float curl=(sin(s*20.0-ph*9.0)*.055+sin(s*37.0+ph*13.0)*.025)*s;
    float radius=engine.w*(.74+.10*sin(ph*5.0))*(.07+.93*pow(max(1.0-s,0.0),.72));
    float q=(across/engine.w-curl)/(radius/engine.w+.001);
    float front=smoothstep(-.16,.04,s);
    float tip=1.0-smoothstep(.68,1.12,s);
    float taper=front*tip;
    float turbulence=.83+.10*sin(s*31.0-ph*13.0+sin(q*5.0+ph*7.0)*.55)+.07*sin(s*64.0+ph*17.0);
    float shell=exp(-q*q*2.2)*taper*turbulence;
    float core=exp(-q*q*12.0)*taper*(1.0-smoothstep(.15,.73,s));
    float halo=exp(-q*q*.38)*taper*.14;
    float3 color=float3(.10,.41,1.0)*(shell*.85+halo)+float3(.62,.90,1.0)*core*.75;
    if(_HasViewEffects>.5&&_UnitKind>3.5)
        color=float3(1.0,.28,.04)*(shell*.85+halo)+float3(1.0,.82,.38)*core*.75;
    // A few dim grains make advection visible without turning idle into an attack.
    for(int i=0;i<4;i++)
    {
        float fi=float(i);
        float u=frac(phase/FLEET_TAU*(2.0+float(i%2))+fi*.271+direction.z*.11);
        float sx=u*engine.z;
        float sy=sin(fi*2.4+u*7.0+direction.z)*engine.w*.34;
        float r=1.1+fi*.23;
        float a=exp(-((along-sx)*(along-sx)+(across-sy)*(across-sy))/(r*r*2.0));
        color+=float3(.35,.75,1.0)*a*pow(sin(u*3.14159265),2.0)*.18;
    }
    return color;
}

// Rear nozzles face the camera: heat remains inside the aperture, with traveling arcs.
// Coordinates follow the nacelle rig so the effect cannot drift off the moving nozzle.
float3 NozzleHeat(float2 p,float4 n,float part,float phase,float offset)
{
    if(n.z<1.0)return float3(0,0,0);
    float2 q=InverseViewMechanism(p,int(part),phase);
    float2 d=(q-n.xy)/n.zw;
    float radius=length(d);
    if(radius>1.0)return float3(0,0,0);
    float angle=atan2(d.y,d.x);
    float pulse=.72+.18*sin(phase*3.0+offset)+.10*sin(phase*7.0+offset);
    float annulus=exp(-pow((radius-.75)/.115,2.0));
    float blades=.48+.52*pow(.5+.5*sin(angle*9.0-phase*4.0+offset),4.0);
    float core=exp(-radius*radius*6.5)*(.56+.12*sin(phase*5.0+radius*17.0));
    float inner=exp(-radius*radius*1.8)*.17;
    return _NozzleColor.rgb*(annulus*(.30+.40*blades)+inner+core*.8)*pulse;
}

float4 IdlePixel(float2 uvTop,float seconds)
{
    float phase=FLEET_TAU*frac(seconds/max(_LoopDuration,.001));
    float2 hover=float2(.20*sin(phase+.55),sin(phase)+.18*sin(phase*2.0+.6))*_HoverPx;
    float2 p=uvTop*_Canvas.xy-hover;
    float4 art,mask;
    ReadRig(p,phase,art,mask);
    float pulse=.56+.22*sin(phase*2.0-.7)+.08*sin(phase*3.0+.8);
    float3 glowColor=_UnitKind>1.5&&_UnitKind<2.5?float3(1.0,.13,.035):float3(.10,.63,1.0);
    if(_UnitKind>2.5&&_UnitKind<3.5) glowColor=float3(1.0,.37,.045);

    if(_HasViewEffects>.5)glowColor=_GlowColor.rgb;

    // Local window illumination. Painted orange stripes and white armor are excluded by mask.
    float flow=pow(.5+.5*sin(mask.a*88.0+phase*3.0),5.0);
    float illumination=mask.r*(.20+pulse*.58);
    if(_UnitKind>2.5&&_UnitKind<3.5) illumination=mask.r*(.12+pulse*.34)+mask.g*flow*.67;
    art.rgb=1.0-(1.0-art.rgb)*exp(-glowColor*illumination*1.45);

    // Shield inlays carry a traveling sheen and a restrained fine lattice.
    if(_UnitKind<.5)
    {
        float traveling=pow(.5+.5*sin(mask.a*16.0-phase*2.0),18.0);
        float lineA=pow(.5+.5*sin((mask.b+mask.a*.58)*145.0),40.0);
        float lineB=pow(.5+.5*sin((mask.b-mask.a*.58)*145.0),40.0);
        float lattice=(lineA+lineB)*.010*(.5+.5*sin(phase-mask.a*5.0));
        float energy=mask.g*(traveling*.13+lattice);
        art.rgb=1.0-(1.0-art.rgb)*exp(-float3(.10,.70,1.0)*energy*1.5);
    }
    else if(_UnitKind<2.5)
    {
        float sheen=pow(.5+.5*sin((mask.b+mask.a*.55)*9.0-phase),36.0);
        art.rgb=1.0-(1.0-art.rgb)*exp(-float3(.32,.65,.90)*mask.g*sheen*.08);
    }

    if(_HasViewEffects>.5)
    {
        float3 heat=NozzleHeat(p,_Nozzle0,_NozzleParts.x,phase,0.0)
                  +NozzleHeat(p,_Nozzle1,_NozzleParts.y,phase,1.1)
                  +NozzleHeat(p,_Nozzle2,_NozzleParts.z,phase,2.2);
        art.rgb=1.0-(1.0-art.rgb)*exp(-heat*2.2);
    }

    // A soft halo around only the registered emissive windows.
    float halo=0.0;
    halo+=ReadMask(p+float2( 7.0, 0.0)).r;
    halo+=ReadMask(p+float2(-7.0, 0.0)).r;
    halo+=ReadMask(p+float2( 0.0, 7.0)).r;
    halo+=ReadMask(p+float2( 0.0,-7.0)).r;
    halo+=ReadMask(p+float2( 6.0, 6.0)).r;
    halo+=ReadMask(p+float2(-6.0, 6.0)).r;
    halo+=ReadMask(p+float2( 6.0,-6.0)).r;
    halo+=ReadMask(p+float2(-6.0,-6.0)).r;
    halo*=.125;
    float3 engine=EngineFlow(p,_Engine0,_Direction0,phase)
                 +EngineFlow(p,_Engine1,_Direction1,phase)
                 +EngineFlow(p,_Engine2,_Direction2,phase);
    float engineAlpha=1.0-exp(-max(engine.r,max(engine.g,engine.b))*.92);
    float3 backRGB=engine*(1.0-exp(-1.0));
    float haloAlpha=halo*(.10+pulse*.09);
    // All exhaust is composited behind the original sprite; no plume cuts across a nozzle rim.
    float alpha=art.a+(1.0-art.a)*engineAlpha;
    float3 rgb=art.rgb*art.a+backRGB*(1.0-art.a);
    rgb+=glowColor*haloAlpha*.35;
    alpha=alpha+(1.0-alpha)*haloAlpha*.35;
    return float4(rgb,clamp(alpha,0.0,1.0));
}

#endif
