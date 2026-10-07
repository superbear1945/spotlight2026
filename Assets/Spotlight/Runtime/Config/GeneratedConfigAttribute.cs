// 只读配置 ScriptableObject 的公共标记与说明。
// 为什么需要：生成的配置资产可能被手工改动，Inspector 必须只读并提示源表位置；
// 由于“只读绘制”属于编辑器职责，这里只定义标记特性与运行时侧的统一接口。
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 标记一个 ScriptableObject 是“由 Excel 编译生成、只读”的配置资产。
    /// 输入：无（特性）；输出：编辑器为带此特性的类型禁用 Inspector 编辑并显示源表提示。
    /// 为什么需要：生成资产手工改数值会破坏“配置来源于工作簿”的约束，
    /// 因此必须让策划在 Inspector 上无法编辑，并在运行/构建门禁中检测篡改。
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class GeneratedConfigAttribute : System.Attribute
    {
        /// <summary>默认源工作簿相对路径，用于在 Inspector 与错误报告中定位来源。</summary>
        public string SourcePath { get; }

        /// <summary>构造标记。输入：默认源文件相对路径（可为空）；输出：可标注在配置类上的特性。</summary>
        public GeneratedConfigAttribute(string sourcePath = "") { SourcePath = sourcePath; }
    }

    /// <summary>
    /// 所有“可追溯到某张表”的生成资产共用的来源描述。
    /// 输入：编译器写入的文件名与工作表名；输出：Inspector 与校验报告统一读取的来源文本。
    /// </summary>
    public interface IConfigSourceInfo
    {
        /// <summary>源工作簿文件名（含扩展名），例如 Spotlight.xlsx。</summary>
        string SourceWorkbook { get; }
        /// <summary>源工作表名，例如 Cards。</summary>
        string SourceSheet { get; }
    }
}
