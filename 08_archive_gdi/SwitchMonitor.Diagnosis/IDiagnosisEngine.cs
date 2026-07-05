using System.Collections.Generic;
using SwitchMonitor.Common;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// 诊断引擎接口。
    /// 所有诊断引擎实现必须实现此接口，以便主程序可以通过依赖注入加载和替换。
    /// 诊断引擎 DLL 可独立编译和替换，不依赖 UI 或 Data 层。
    /// </summary>
    public interface IDiagnosisEngine
    {
        /// <summary>
        /// 初始化诊断引擎，加载规则配置文件
        /// </summary>
        /// <param name="rulesPath">规则配置目录路径</param>
        void Initialize(string rulesPath);

        /// <summary>
        /// 对一次道岔动作数据进行诊断分析
        /// </summary>
        /// <param name="data">道岔动作数据包</param>
        /// <returns>诊断结论列表（可能有多条规则同时触发）</returns>
        List<DiagnosisResult> Diagnose(SwitchActionData data);
    }
}
