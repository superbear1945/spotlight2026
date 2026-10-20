// 只读配置 ScriptableObject 的公共标记与说明。
// 为什么需要：生成的配置资产可能被手工改动，Inspector 必须只读并提示源表位置；
// 由于“只读绘制”属于编辑器职责，这里只定义标记特性与运行时侧的统一接口。
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 标记一个 ScriptableObject 是“由 Excel 编译生成、只读”的配置资产。
    /// 输入：无（纯标记特性）；输出：编辑器为带此特性的类型禁用 Inspector 编辑。
    /// 为什么需要：生成资产手工改数值会破坏“配置来源于工作簿”的约束，
    /// 因此必须让策划在 Inspector 上无法编辑，并在运行/构建门禁中检测篡改。
    /// 为什么不带源文件路径参数：特性参数会被编译进运行时程序集，
    /// 正式包拆开后就能看到“Assets/Spotlight/Config/Spotlight.xlsx”这类开发期路径；
    /// 溯源信息只写在不进包的 ConfigManifest。
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class GeneratedConfigAttribute : System.Attribute
    {
    }
}
