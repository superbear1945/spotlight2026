// 统一伤害结算的纯数据契约。
// 为什么单独成文件：普通攻击、炸弹与后续“伤害标记追加伤害”必须共用同一套事实记录，
// 否则日志、表现与 AI 会各自从快照反推“谁造成了多少伤害”，出现三种口径。
// 约束：本文件只放不可变数据与枚举，不引用 UnityEngine；真正的扣血步骤在 GameSession 内部。
using System;
using System.Collections.Generic;

namespace Spotlight
{
    /// <summary>
    /// 一笔伤害的类型。
    /// 输入：规则内核结算时决定；输出：区分“直接伤害”与“由伤害标记触发的追加伤害”。
    /// 为什么需要 MarkBonus：追加伤害绝不能再触发伤害标记或再次追加，否则会无限递归；
    /// 只有 DirectAttack 可以让规则层回到“连杀恢复行动”等攻击后处理。
    /// </summary>
    public enum DamageKind
    {
        /// <summary>普通攻击造成的基础伤害；可能是攻击者当前攻击力的全部一次结算。</summary>
        DirectAttack,
        /// <summary>炸弹每消耗 1 点预算造成的基础伤害。</summary>
        Bomb,
        /// <summary>目标身上已有伤害标记触发的追加伤害；不递归、不挂新标记、不消耗炸弹预算。</summary>
        MarkBonus
    }

    /// <summary>
    /// 一次直接伤害的归属上下文。
    /// 输入：规则内核在结算前构造；输出：伤害结果、日志与“击杀恢复行动”判定共同读取的归属信息。
    /// 为什么需要根类型 RootKind：追加伤害沿用触发本次受伤的根来源（炸弹仍是炸弹、攻击仍是攻击），
    /// 而不是最初的标记施加者；这样“炸弹追加”不会让维京长船恢复行动。
    /// </summary>
    public sealed class DamageContext
    {
        /// <summary>伤害归属阵营；炸弹固定为 Boss 阵营。</summary>
        public Side SourceSide { get; }
        /// <summary>来源单位 ID；炸弹没有来源单位为 null（不伪装成某个施加标记的单位）。</summary>
        public long? SourceUnitId { get; }
        /// <summary>被结算的目标单位 ID；家不使用该字段（家不参与伤害标记）。</summary>
        public long TargetId { get; }
        /// <summary>本笔伤害的类型。</summary>
        public DamageKind Kind { get; }
        /// <summary>整组直接伤害的根类型；追加伤害继承构造时的根类型。</summary>
        public DamageKind RootKind { get; }
        /// <summary>炸弹信息；非炸弹伤害为 null。</summary>
        public BossBombState Bomb { get; }

        /// <summary>
        /// 构造一次伤害上下文。
        /// 输入：来源阵营、来源单位（可空）、目标、本笔类型，以及可选的根类型与炸弹信息；
        /// 输出：不可变上下文。未显式给出根类型时默认与本笔类型相同。
        /// </summary>
        public DamageContext(Side sourceSide, long? sourceUnitId, long targetId, DamageKind kind, DamageKind? rootKind = null, BossBombState bomb = null)
        {
            SourceSide = sourceSide;
            SourceUnitId = sourceUnitId;
            TargetId = targetId;
            Kind = kind;
            RootKind = rootKind ?? kind;
            Bomb = bomb;
        }

        /// <summary>
        /// 派生“追加伤害”上下文。
        /// 输入：追加伤害类型（通常为 MarkBonus）；输出：保留同一来源、同一目标与同一根类型的新上下文。
        /// 为什么不用原地修改：上下文是不可变数据，同一整组伤害中的每一笔都需要能独立记录。
        /// </summary>
        public DamageContext WithKind(DamageKind kind) => new DamageContext(SourceSide, SourceUnitId, TargetId, kind, RootKind, Bomb);
    }

    /// <summary>
    /// 目标身上的一层永久伤害标记。
    /// 输入：施加时从来源配置复制的每层追加伤害值；输出：之后每次直接受伤时按此值追加。
    /// 为什么保存数值副本而不是来源引用：标记是永久状态，来源单位离场、跨回合或配置被调试修改后
    /// 都不能改变已经挂上的层；不同来源、不同配置的层可以同时存在且各自保留自己的数值。
    /// </summary>
    public sealed class DamageMarkLayer
    {
        /// <summary>本层在每次直接受伤后追加的伤害值（非负整数）。</summary>
        public int Damage { get; }

        /// <summary>构造一层标记。输入：施加时的每层追加伤害；输出：不可变层数据，负值按 0 处理。</summary>
        public DamageMarkLayer(int damage) { Damage = damage < 0 ? 0 : damage; }
    }

    /// <summary>
    /// 一次直接伤害（含其全部追加伤害）的事实结果。
    /// 输入：由规则内核结算后创建；输出：攻击分支、击杀恢复、炸弹步骤、日志与测试读取的唯一定量事实。
    /// 为什么不用“扣血前后快照差”推断：目标可能在同一次命令中被多次伤害，
    /// 快照差无法区分“哪一份属于本次攻击”，也无法知道追加伤害是否触发了标记。
    /// </summary>
    public sealed class DamageResult
    {
        /// <summary>类型（本笔伤害的类型）；追加结算时为 MarkBonus。</summary>
        public DamageKind Kind { get; }
        /// <summary>整组伤害的根类型；追加伤害沿用根来源，便于判断“这次击杀是谁造成的”。</summary>
        public DamageKind RootKind { get; }
        /// <summary>伤害归属阵营。</summary>
        public Side SourceSide { get; }
        /// <summary>来源单位 ID；炸弹为 null。</summary>
        public long? SourceUnitId { get; }
        /// <summary>目标单位 ID。</summary>
        public long TargetId { get; }
        /// <summary>基础伤害实际扣掉的生命值（不会超过目标剩余生命）。</summary>
        public int BaseDamage { get; }
        /// <summary>由已有伤害标记追加、实际扣掉的生命值总和。</summary>
        public int BonusDamage { get; }
        /// <summary>本次整组伤害实际扣掉的总生命值；炸弹预算不吃追加部分。</summary>
        public int TotalDamage => BaseDamage + BonusDamage;
        /// <summary>目标是否因本次整组伤害死亡（死亡只结算一次）。</summary>
        public bool Killed { get; }
        /// <summary>本次受伤前已存在的标记层副本；新挂的层不在其中，因此“本次受伤按受伤前层数结算”可复现。</summary>
        public IReadOnlyList<DamageMarkLayer> AppliedLayers { get; }
        /// <summary>炸弹信息；非炸弹伤害为 null。</summary>
        public BossBombState Bomb { get; }

        /// <summary>
        /// 构造一次伤害结果。
        /// 输入：上下文与结算得到的实际数值、死亡标记、本次使用的层副本；
        /// 输出：不可变结果，集合被复制为独立只读数组，旧结果不会被后续挂层修改。
        /// 说明：只有规则内核可以创建（internal），表现层与测试只读取。
        /// </summary>
        internal DamageResult(DamageContext context, int baseDamage, int bonusDamage, bool killed, IReadOnlyList<DamageMarkLayer> layers)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            Kind = context.Kind;
            RootKind = context.RootKind;
            SourceSide = context.SourceSide;
            SourceUnitId = context.SourceUnitId;
            TargetId = context.TargetId;
            BaseDamage = baseDamage < 0 ? 0 : baseDamage;
            BonusDamage = bonusDamage < 0 ? 0 : bonusDamage;
            Killed = killed;
            Bomb = context.Bomb;
            var copy = layers == null ? Array.Empty<DamageMarkLayer>() : new DamageMarkLayer[layers.Count];
            for (var i = 0; i < copy.Length; i++) copy[i] = layers[i];
            AppliedLayers = Array.AsReadOnly(copy);
        }
    }
}
