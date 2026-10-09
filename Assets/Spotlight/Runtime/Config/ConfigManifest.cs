// 编译清单资产：记录“这套生成产物来自哪份工作簿的哪个版本”。
// 为什么需要：构建/进入 Play 前必须能判断配置是否过期或被手工篡改，
// 只有保存源文件摘要与生成资产清单，门禁才能给出确定的结论。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 配置编译清单。
    /// 输入：编译器写入的源路径、源摘要、编译时间与生成资产清单；
    /// 输出：门禁与菜单读取的校验依据。
    /// </summary>
    public sealed class ConfigManifest : ScriptableObject
    {
        /// <summary>源工作簿相对路径。</summary>
        [SerializeField] string _sourceWorkbookPath;
        /// <summary>编译时源工作簿的 SHA256 摘要。</summary>
        [SerializeField] string _sourceHash;
        /// <summary>编译时间（UTC ISO 8601 文本）。</summary>
        [SerializeField] string _compiledAtUtc;
        /// <summary>本次编译生成的资产路径集合。</summary>
        [SerializeField] string[] _generatedPaths;

        /// <summary>源工作簿相对路径。</summary>
        public string SourceWorkbookPath => _sourceWorkbookPath;
        /// <summary>编译时源摘要。</summary>
        public string SourceHash => _sourceHash;
        /// <summary>编译时间文本。</summary>
        public string CompiledAtUtc => _compiledAtUtc;
        /// <summary>生成的资产路径集合。</summary>
        public IReadOnlyList<string> GeneratedPaths => _generatedPaths ?? Array.Empty<string>();

        /// <summary>
        /// 写入清单内容。
        /// 输入：源路径、源摘要、编译时间与生成路径；输出：无。
        /// 只允许编译器调用。
        /// </summary>
        public void EditorApply(string workbookPath, string sourceHash, string compiledAtUtc, IEnumerable<string> generatedPaths)
        {
            _sourceWorkbookPath = workbookPath;
            _sourceHash = sourceHash;
            _compiledAtUtc = compiledAtUtc;
            _generatedPaths = generatedPaths == null ? Array.Empty<string>() : new List<string>(generatedPaths).ToArray();
        }
    }
}
