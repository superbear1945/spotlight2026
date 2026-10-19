using System;
using System.Collections.Generic;
using System.Linq;

namespace Spotlight
{
    /// <summary>通用刷怪配置契约。输入为卡种定义，输出为生成卡种引用；不保存计时或场上状态。</summary>
    public interface ISpawnConfig
    {
        /// <summary>每个所属阵营回合尝试生成的攻击类卡种 ID。</summary>
        string SpawnCardType { get; }
    }

    /// <summary>刷怪配置不可变副本。用于隔离 Excel/JSON 草稿与正在运行的对局。</summary>
    sealed class SpawnValues : ISpawnConfig
    {
        /// <inheritdoc />
        public string SpawnCardType { get; }
        /// <summary>复制生成卡种引用；输入为稳定 ID，输出为只读配置，不绑定任何具体卡名。</summary>
        public SpawnValues(string cardType) { SpawnCardType = cardType; }
    }

    /// <summary>刷怪位置解析器。只查询棋盘并选择位置；移除、部署和事件由唯一状态所有者 GameSession 结算。</summary>
    internal static class SpawnResolver
    {
        /// <summary>输入为来源、棋盘尺寸、占位查询和随机源；输出为空格或可顶替敌方单位格，无位置返回 null。家永不顶替。</summary>
        internal static Cell? ChooseCell(UnitState source, int rows, int columns,
            Func<Cell, bool> occupied, IReadOnlyList<UnitState> units, IRandomSource random)
        {
            var neighbors = new List<Cell>();
            for (var row = -1; row <= 1; row++)
                for (var col = -1; col <= 1; col++)
                {
                    var cell = new Cell(source.Position.Row + row, source.Position.Col + col);
                    if ((row != 0 || col != 0) && cell.Row >= 0 && cell.Row < rows && cell.Col >= 0 && cell.Col < columns)
                        neighbors.Add(cell);
                }
            var candidates = neighbors.Where(p => !occupied(p)).ToArray();
            if (candidates.Length == 0)
                candidates = neighbors.Where(p => units.Any(u => source.Owner == Side.Boss && u.Owner == Side.Player && u.Position == p)).ToArray();
            return candidates.Length == 0 ? (Cell?)null : candidates[random.Next(candidates.Length)];
        }
    }
}
